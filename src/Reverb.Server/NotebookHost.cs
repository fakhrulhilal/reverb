using System.Text.Json;
using System.Threading.Channels;
using Reverb.Format;
using Reverb.Kernel;

namespace Reverb.Server;

public sealed record EditorOptions
{
    public required string NotebookPath { get; init; }

    /// <summary>The assembly that runs the kernel with its <c>__kernel</c> command (the CLI).</summary>
    public required string KernelEntryAssembly { get; init; }

    /// <summary>The Reverb.Runtime version to add to notebooks that don't reference it yet.</summary>
    public string RuntimeVersion { get; init; } = "0.1.0";

    public string Host { get; init; } = "127.0.0.1";

    /// <summary>0 picks a free port.</summary>
    public int Port { get; init; }

    /// <summary>The access token. A random one is made when this is null.</summary>
    public string? Token { get; init; }

    public TimeSpan AutosaveDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Serve the notebook as an app (<c>reverb run</c>): outputs and widgets only. Code isn't sent
    /// to the browser, nothing can be edited, the file is never written, and lazy mode is off.
    /// </summary>
    public bool AppMode { get; init; }

    public TextWriter Log { get; init; } = TextWriter.Null;
}

/// <summary>A browser tab connected to the notebook.</summary>
public sealed class ClientSession(Action<ServerMessage> send)
{
    internal Action<ServerMessage> Send { get; } = send;

    /// <summary>The last <see cref="ClientMessage.Seq"/> handled for this tab.</summary>
    internal long LastSeq { get; set; }
}

/// <summary>
/// One open notebook: the document as the editor has it, the file on disk, the kernel process and
/// what it last reported, and the connected tabs. Everything runs on one logical thread: browser
/// messages, kernel events, timers and file changes are all queued and handled in order.
/// </summary>
/// <remarks>
/// The editor's text and the kernel's code differ on purpose. Typing changes the document (and is
/// saved), but a cell's new code only reaches the kernel when the cell is run, the notebook is run
/// all, or the file changes on disk. Structural changes (add, delete, move, disable) apply at once.
/// </remarks>
public sealed class NotebookHost : IAsyncDisposable
{
    sealed class CellRuntime
    {
        public string Status = "stale";
        public long? ElapsedMs;
        public string? Message;
        public List<OutputData> Outputs = [];

        /// <summary>How many times the cell has started running in this session.</summary>
        public int Runs;
    }

    readonly EditorOptions options;
    readonly Channel<Func<Task>> inbox = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    readonly Task loop;
    readonly List<ClientSession> clients = [];
    readonly Dictionary<string, string> kernelSources = new(StringComparer.Ordinal);
    readonly Dictionary<string, CellRuntime> runtime = new(StringComparer.Ordinal);
    readonly Timer saveTimer;
    readonly FileWatcher watcher;

    NotebookDocument doc;
    string? diskText;
    int saveFailures;
    int version;
    int analyzedVersion = -1;
    List<DiagnosticData> diagnostics = [];
    List<EdgeData> graph = [];

    KernelClient? kernel;
    int kernelGeneration;
    int outstanding;
    string kernelState = "starting";
    string? kernelMessage;
    bool kernelLazy;
    bool disposed;
    readonly List<DateTime> crashes = [];

    // A widget change that's still running, and the newest value that arrived meanwhile.
    readonly HashSet<string> widgetsInFlight = new(StringComparer.Ordinal);
    readonly Dictionary<string, JsonElement?> widgetsWaiting = new(StringComparer.Ordinal);

    NotebookHost(EditorOptions options, NotebookDocument doc, string? diskText)
    {
        this.options = options;
        this.doc = doc;
        this.diskText = diskText;
        Path = System.IO.Path.GetFullPath(options.NotebookPath);
        saveTimer = new Timer(_ => Post(() => SaveNow()));
        watcher = new FileWatcher(Path, () => Post(CheckFileAsync));
        loop = Task.Run(RunLoopAsync);
    }

    public string Path { get; }

    /// <summary>The document as the editor has it. Read it from the host's thread or after <see cref="IdleAsync"/>.</summary>
    public NotebookDocument Document => doc;

    public int Version => version;

    /// <summary>The kernel's process id, for tests that kill it.</summary>
    public int? KernelProcessId => kernel?.ProcessId;

    public static async Task<NotebookHost> OpenAsync(EditorOptions options)
    {
        var path = System.IO.Path.GetFullPath(options.NotebookPath);
        var text = NotebookFile.ReadText(path);
        var host = new NotebookHost(options, Load(text, options.RuntimeVersion), text);
        await host.InvokeAsync(() => host.StartKernelAsync(run: true));
        return host;
    }

    /// <summary>Parses a file and brings it up to date in memory. The file itself is only rewritten when it's next saved.</summary>
    static NotebookDocument Load(string text, string runtimeVersion)
    {
        // A fixed seed: an unmarked file gets the same cell ids every time it's read.
        var (upgraded, _) = NotebookUpgrader.Upgrade(NotebookDocument.Parse(text), new UpgradeOptions { RuntimeVersion = runtimeVersion, Random = new Random(0) });
        return upgraded;
    }

    // ---- the host's thread ----------------------------------------------------------------------

    void Post(Func<Task> work) => inbox.Writer.TryWrite(work);

    void Post(Action work) => Post(() =>
    {
        work();
        return Task.CompletedTask;
    });

    Task InvokeAsync(Func<Task> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async () =>
        {
            try
            {
                await work();
                done.TrySetResult();
            }
            catch (Exception e)
            {
                done.TrySetException(e);
            }
        });
        return done.Task;
    }

    /// <summary>Completes when everything queued so far has been handled.</summary>
    public Task IdleAsync() => InvokeAsync(() => Task.CompletedTask);

    async Task RunLoopAsync()
    {
        await foreach (var work in inbox.Reader.ReadAllAsync())
        {
            try
            {
                await work();
            }
            catch (Exception e)
            {
                options.Log.WriteLine($"reverb: internal error: {e}");
                Broadcast(new ServerMessage { Type = BrowserMessages.Error, Message = $"Something went wrong in the server: {e.Message}" });
            }
        }
    }

    // ---- browser tabs ---------------------------------------------------------------------------

    public ClientSession Connect(Action<ServerMessage> send)
    {
        var client = new ClientSession(send);
        Post(() =>
        {
            clients.Add(client);
            client.Send(Snapshot(client));
        });
        return client;
    }

    public void Disconnect(ClientSession client) => Post(() => { clients.Remove(client); });

    public void Receive(ClientSession client, ClientMessage message) => Post(() => HandleAsync(client, message));

    void Broadcast(ServerMessage message)
    {
        foreach (var c in clients) c.Send(message);
    }

    void BroadcastSnapshot()
    {
        foreach (var c in clients) c.Send(Snapshot(c));
    }

    async Task HandleAsync(ClientSession client, ClientMessage m)
    {
        client.LastSeq = Math.Max(client.LastSeq, m.Seq);
        if (options.AppMode && m.Type != BrowserMessages.WidgetSetValue)
        {
            client.Send(Error("This notebook is running as an app, so it can't be changed here. Open it with 'reverb edit' to edit it."));
            return;
        }
        switch (m.Type)
        {
            case BrowserMessages.CellUpdate:
                if (m.CellId is null || m.Source is null || doc.FindCell(m.CellId) is not { } cell) return;
                var source = NotebookDocument.NormalizeSource(m.Source);
                if (cell.Source == source)
                {
                    // Nothing changed (typed and undone, say), but the tab still waits to hear it's saved.
                    saveTimer.Change(options.AutosaveDelay, Timeout.InfiniteTimeSpan);
                    return;
                }
                doc = doc.WithCellSource(m.CellId, source);
                Changed(saveNow: false);
                break;

            case BrowserMessages.CellRun:
                if (m.CellId is null || doc.FindCell(m.CellId) is not { } run) return;
                if (run.Kind == CellKind.Markdown) return;
                var changed = Submit(run);
                if (run.Kind != CellKind.Code)
                {
                    if (changed) SendUpdate();
                }
                else if (changed || kernelLazy)
                {
                    SendUpdate(runCell: run.Key);
                }
                else
                {
                    SendWork(new KernelRequest { Type = RequestTypes.Run, Cell = run.Key });
                }
                break;

            case BrowserMessages.RunAll:
                foreach (var c in doc.Cells) Submit(c);
                SendUpdate(runAll: true);
                break;

            case BrowserMessages.SetLazy:
                doc = doc.WithHeaderFlag("lazy", m.Lazy ?? false);
                SyncLazy();
                Changed(saveNow: true);
                BroadcastSnapshot();
                break;

            case BrowserMessages.Save:
                SaveNow();
                break;

            case BrowserMessages.CellCreate:
                CreateCell(m);
                break;

            case BrowserMessages.CellDelete:
                var di = m.CellId is null ? -1 : doc.IndexOf(m.CellId);
                if (di < 0) return;
                if (doc.Cells[di].Kind == CellKind.Setup)
                {
                    client.Send(Error("The setup cell can't be deleted. It holds the notebook's using directives."));
                    return;
                }
                doc = doc.RemoveCell(di);
                kernelSources.Remove(m.CellId!);
                runtime.Remove(m.CellId!);
                Structural();
                break;

            case BrowserMessages.CellMove:
                MoveCell(client, m);
                break;

            case BrowserMessages.CellSetAttrs:
                var ai = m.CellId is null || m.Attrs is null ? -1 : doc.IndexOf(m.CellId);
                if (ai < 0) return;
                var target = doc.Cells[ai];
                if (m.Attrs!.HideCode is { } hide) target = target.WithFlag("hide_code", hide);
                if (m.Attrs.Disabled is { } disabled) target = target.WithFlag("disabled", disabled);
                if (m.Attrs.Name is { } name) target = name.Length == 0 ? target.WithoutAttribute("name") : target.WithAttribute("name", name);
                doc = doc.ReplaceCell(ai, target);
                Structural();
                break;

            case BrowserMessages.WidgetSetValue:
                if (m.WidgetId is null) return;
                SetWidget(m.WidgetId, m.Value, everyChangeCounts: m.Kind == "button");
                break;

            case BrowserMessages.KernelInterrupt:
                kernel?.Interrupt();
                break;

            case BrowserMessages.KernelRestart:
                await RestartKernelAsync("Restarted.");
                break;

            default:
                client.Send(Error($"Unknown message type '{m.Type}'."));
                break;
        }
    }

    static ServerMessage Error(string message) => new() { Type = BrowserMessages.Error, Message = message };

    void CreateCell(ClientMessage m)
    {
        var kind = m.Kind == "markdown" ? CellKind.Markdown : CellKind.Code;
        var taken = doc.Cells.Select(c => c.Id).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var id = m.CellId is { } wanted && CellIds.IsValid(wanted) && !CellIds.IsReserved(wanted) && !taken.Contains(wanted)
            ? wanted
            : CellIds.New(Random.Shared, taken);
        var index = InsertionIndex(doc, m.AfterCellId);
        var cell = kind == CellKind.Markdown ? NotebookCell.Markdown(id, m.Source ?? "") : NotebookCell.Code(id, m.Source ?? "");
        doc = doc.InsertCell(index, cell);
        Submit(doc.Cells[index]);
        Structural();
    }

    void MoveCell(ClientSession client, ClientMessage m)
    {
        var from = m.CellId is null ? -1 : doc.IndexOf(m.CellId);
        if (from < 0) return;
        var cell = doc.Cells[from];
        if (cell.Kind is CellKind.Setup or CellKind.Types)
        {
            client.Send(Error("The setup cell stays first and the types cell stays last."));
            return;
        }
        if (m.AfterCellId == m.CellId) return;
        var without = doc.RemoveCell(from);
        doc = without.InsertCell(InsertionIndex(without, m.AfterCellId), cell);
        Structural();
    }

    /// <summary>Where a new or moved cell goes: after the given cell (or first), but never before setup or after types.</summary>
    static int InsertionIndex(NotebookDocument doc, string? afterCellId)
    {
        var first = doc.Cells.Length > 0 && doc.Cells[0].Kind == CellKind.Setup ? 1 : 0;
        var last = doc.Cells.Length > 0 && doc.Cells[^1].Kind == CellKind.Types ? doc.Cells.Length - 1 : doc.Cells.Length;
        var index = afterCellId is null ? first : doc.IndexOf(afterCellId) + 1;
        if (index <= 0) index = first;
        return Math.Clamp(index, first, last);
    }

    /// <summary>After a structural change: tell the kernel, save, and resend the notebook.</summary>
    void Structural()
    {
        Changed(saveNow: true);
        SendUpdate();
        BroadcastSnapshot();
    }

    /// <summary>The document changed: bump the version, analyse it and schedule a save.</summary>
    void Changed(bool saveNow)
    {
        version++;
        Analyze();
        if (saveNow) SaveNow();
        else saveTimer.Change(options.AutosaveDelay, Timeout.InfiniteTimeSpan);
    }

    // ---- what the kernel has ----------------------------------------------------------------------

    /// <summary>Hands the cell's current code to the kernel. Returns true if it differs from what the kernel had.</summary>
    bool Submit(NotebookCell cell)
    {
        if (cell.Kind == CellKind.Markdown || cell.Key is not { } key) return false;
        if (kernelSources.TryGetValue(key, out var had) && had == cell.Source) return false;
        kernelSources[key] = cell.Source;
        return true;
    }

    void SubmitAll()
    {
        kernelSources.Clear();
        foreach (var c in doc.Cells) Submit(c);
    }

    bool IsEdited(NotebookCell cell) =>
        cell.Kind != CellKind.Markdown && cell.Key is { } key && kernelSources.TryGetValue(key, out var had) && had != cell.Source;

    /// <summary>The notebook as the kernel should see it: current structure, each cell's last submitted code.</summary>
    NotebookDocument KernelDocument() => doc with
    {
        Cells = [.. doc.Cells.Select(c => c.Key is { } key && c.Kind != CellKind.Markdown && kernelSources.TryGetValue(key, out var src) ? c with { Source = src } : c)],
    };

    void SendUpdate(string? runCell = null, bool runAll = false) =>
        SendWork(new KernelRequest { Type = RequestTypes.Update, Text = KernelDocument().Serialize(), Cell = runCell, Run = runAll ? true : null });

    void Analyze()
    {
        if (kernel is null || kernelState == "dead" || options.AppMode) return;
        var request = new KernelRequest { Type = RequestTypes.Analyze, Text = doc.Serialize(), Version = version };
        _ = Swallow(kernel.SendAsync(request));
    }

    bool LazyInFile => !options.AppMode && (doc.Preamble.Header?.Lazy ?? false);

    void SyncLazy()
    {
        var lazy = LazyInFile;
        if (lazy == kernelLazy) return;
        kernelLazy = lazy;
        SendWork(new KernelRequest { Type = RequestTypes.SetLazy, Lazy = lazy });
    }

    /// <summary>
    /// Sends a widget change. While one change to a widget is running, later ones wait and only
    /// the newest is sent, so dragging a slider doesn't queue up a run for every pixel. Button
    /// clicks all count, so they're sent as they come.
    /// </summary>
    void SetWidget(string id, JsonElement? value, bool everyChangeCounts)
    {
        if (!everyChangeCounts && widgetsInFlight.Contains(id))
        {
            widgetsWaiting[id] = value;
            return;
        }
        widgetsInFlight.Add(id);
        SendWork(new KernelRequest { Type = RequestTypes.SetWidget, WidgetId = id, Value = value }, () =>
        {
            widgetsInFlight.Remove(id);
            if (widgetsWaiting.Remove(id, out var next)) SetWidget(id, next, everyChangeCounts: false);
        });
    }

    /// <summary>Sends a request that runs code. The kernel shows as busy until it's done.</summary>
    void SendWork(KernelRequest request, Action? done = null)
    {
        if (kernel is null || kernelState == "dead") return;
        var generation = kernelGeneration;
        outstanding++;
        SetKernelState("busy", null);
        var task = kernel.SendAsync(request);
        _ = task.ContinueWith(_ => Post(() =>
        {
            if (generation != kernelGeneration) return;
            outstanding = Math.Max(0, outstanding - 1);
            if (outstanding == 0 && kernelState == "busy") SetKernelState("ready", null);
            done?.Invoke();
        }), TaskScheduler.Default);
    }

    static async Task Swallow(Task task)
    {
        try
        {
            await task;
        }
        catch (KernelExitedException)
        {
        }
    }

    void SetKernelState(string state, string? message)
    {
        if (state == kernelState && message == kernelMessage) return;
        kernelState = state;
        kernelMessage = message;
        Broadcast(new ServerMessage { Type = BrowserMessages.KernelStatus, State = state, Message = message });
    }

    // ---- the kernel process -----------------------------------------------------------------------

    /// <summary>Starts a kernel and opens the notebook in it. <paramref name="run"/> runs it too (unless it's lazy).</summary>
    async Task StartKernelAsync(bool run)
    {
        var generation = ++kernelGeneration;
        outstanding = 0;
        SetKernelState("starting", kernelMessage);
        KernelClient client;
        try
        {
            client = await KernelClient.StartAsync(new KernelLaunch
            {
                EntryAssembly = options.KernelEntryAssembly,
                NotebookPath = Path,
                Directives = doc.Preamble.Directives,
            });
        }
        catch (Exception e) when (e is KernelExitedException or IOException or System.ComponentModel.Win32Exception)
        {
            SetKernelState("dead", $"The kernel didn't start: {e.Message}");
            return;
        }

        client.Event += e => Post(() => OnKernelEvent(generation, e));
        client.StrayOutput += line => options.Log.WriteLine(line);
        client.Exited += code => Post(() => OnKernelExited(generation, code));
        kernel = client;
        if (disposed)
        {
            await client.DisposeAsync();
            return;
        }

        SetKernelState("ready", kernelMessage);
        kernelLazy = LazyInFile;
        SubmitAll();
        SendWork(new KernelRequest { Type = RequestTypes.Open, Text = KernelDocument().Serialize(), Lazy = kernelLazy, Run = run });
        Analyze();
    }

    async Task RestartKernelAsync(string message)
    {
        var old = kernel;
        kernel = null;
        kernelGeneration++;
        widgetsInFlight.Clear();
        widgetsWaiting.Clear();
        if (old is not null)
        {
            old.Kill();
            await old.DisposeAsync();
        }
        MarkAllStale();
        kernelMessage = message;
        // In the editor nothing runs until asked: if a cell stopped the kernel, running it again
        // would just do it again. An app's viewers can't run cells, so an app runs everything.
        await StartKernelAsync(run: options.AppMode);
        BroadcastSnapshot();
    }

    void OnKernelExited(int generation, int exitCode)
    {
        if (generation != kernelGeneration || disposed) return;
        var stderr = kernel?.StandardError.Trim() ?? "";
        options.Log.WriteLine($"reverb: the kernel stopped (exit code {exitCode}).{(stderr.Length > 0 ? "\n" + stderr : "")}");
        crashes.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromMinutes(1));
        crashes.Add(DateTime.UtcNow);
        if (crashes.Count >= 3)
        {
            // Something keeps killing it (a cell calling Environment.Exit, say). Stop trying.
            MarkAllStale();
            SetKernelState("dead", $"The kernel stopped (exit code {exitCode}) three times in a minute, so Reverb stopped restarting it.{(options.AppMode ? "" : " Fix the cell that stops it, then press Restart.")}");
            return;
        }
        SetKernelState("dead", $"The kernel stopped (exit code {exitCode}). Starting a new one; run cells again to get their values back.");
        Post(() => RestartKernelAsync($"The kernel stopped (exit code {exitCode}) and was restarted. Run cells to get their values back."));
    }

    void MarkAllStale()
    {
        foreach (var cell in doc.Cells.Where(c => c.Kind == CellKind.Code && c.Key is not null))
        {
            var r = Runtime(cell.Key!);
            if (r.Status is "stale" or "disabled") continue;
            r.Status = "stale";
            r.ElapsedMs = null;
            r.Message = null;
            Broadcast(new ServerMessage { Type = BrowserMessages.CellStatus, CellId = cell.Key, Status = "stale" });
        }
    }

    CellRuntime Runtime(string key)
    {
        if (!runtime.TryGetValue(key, out var r)) runtime[key] = r = new CellRuntime();
        return r;
    }

    void OnKernelEvent(int generation, KernelEvent e)
    {
        if (generation != kernelGeneration) return;
        switch (e.Type)
        {
            case EventTypes.Status when e.Cell is not null:
            {
                var r = Runtime(e.Cell);
                if (e.Status == "running" && r.Outputs.Count > 0)
                {
                    // A new kernel doesn't know about outputs from the last one, so clear them here.
                    r.Outputs = [];
                    Broadcast(new ServerMessage { Type = BrowserMessages.CellOutputs, CellId = e.Cell, Outputs = [] });
                }
                if (e.Status == "running") r.Runs++;
                r.Status = e.Status ?? "idle";
                r.ElapsedMs = e.ElapsedMs;
                r.Message = e.Message;
                Broadcast(new ServerMessage { Type = BrowserMessages.CellStatus, CellId = e.Cell, Status = r.Status, ElapsedMs = e.ElapsedMs, Message = e.Message, Runs = r.Runs });
                break;
            }
            case EventTypes.Cleared when e.Cell is not null:
                Runtime(e.Cell).Outputs = [];
                Broadcast(new ServerMessage { Type = BrowserMessages.CellOutputs, CellId = e.Cell, Outputs = [] });
                break;
            case EventTypes.Stream when e.Cell is not null:
            {
                var outputs = Runtime(e.Cell).Outputs;
                if (outputs.Count > 0 && outputs[^1].Stream == e.Stream) outputs[^1].Text += e.Text;
                else outputs.Add(new OutputData { Stream = e.Stream, Text = e.Text });
                Broadcast(new ServerMessage { Type = BrowserMessages.CellStream, CellId = e.Cell, Stream = e.Stream, Text = e.Text });
                break;
            }
            case EventTypes.Display when e.Cell is not null && e.Output is not null:
            {
                var outputs = Runtime(e.Cell).Outputs;
                outputs.Add(e.Output);
                Broadcast(new ServerMessage { Type = BrowserMessages.CellOutputs, CellId = e.Cell, Outputs = outputs });
                break;
            }
            case EventTypes.Outputs when e.Cell is not null:
                Runtime(e.Cell).Outputs = e.Outputs ?? [];
                Broadcast(new ServerMessage { Type = BrowserMessages.CellOutputs, CellId = e.Cell, Outputs = Runtime(e.Cell).Outputs });
                break;
            case EventTypes.Analysis:
                if ((e.Version ?? -1) < analyzedVersion) return;
                analyzedVersion = e.Version ?? analyzedVersion;
                diagnostics = e.Diagnostics ?? [];
                graph = e.Graph ?? [];
                BroadcastAnalysis();
                break;
            case EventTypes.Error:
                options.Log.WriteLine($"reverb: kernel error: {e.Message}");
                break;
        }
    }

    void BroadcastAnalysis()
    {
        foreach (var cell in doc.Cells)
        {
            if (cell.Key is not { } key) continue;
            Broadcast(new ServerMessage { Type = BrowserMessages.CellDiagnostics, CellId = key, NotebookVersion = analyzedVersion, Diagnostics = DiagnosticsOf(key) });
        }
        Broadcast(new ServerMessage { Type = BrowserMessages.CellDiagnostics, NotebookVersion = analyzedVersion, Diagnostics = DiagnosticsOf(null) });
        Broadcast(new ServerMessage { Type = BrowserMessages.GraphUpdate, NotebookVersion = analyzedVersion, Edges = graph });
    }

    List<DiagnosticData> DiagnosticsOf(string? key) => [.. diagnostics.Where(d => d.Cell == key)];

    // ---- snapshots --------------------------------------------------------------------------------

    ServerMessage Snapshot(ClientSession client) => new()
    {
        Type = BrowserMessages.Snapshot,
        NotebookVersion = version,
        Ack = client.LastSeq,
        Path = Path,
        Name = System.IO.Path.GetFileName(Path),
        Mode = options.AppMode ? "app" : "edit",
        Lazy = LazyInFile,
        Kernel = new KernelView { State = kernelState, Message = kernelMessage },
        Packages = [.. doc.Preamble.Directives.Where(d => d.Kind == "package").Select(d => new PackageView { Name = d.Name, Version = d.Value })],
        NotebookDiagnostics = options.AppMode ? [] : DiagnosticsOf(null),
        Edges = options.AppMode ? [] : graph,
        Cells = [.. doc.Cells.Where(c => c.Key is not null && (!options.AppMode || c.Kind is CellKind.Code or CellKind.Markdown)).Select(CellViewOf)],
    };

    CellView CellViewOf(NotebookCell cell)
    {
        var key = cell.Key!;
        var r = cell.Kind == CellKind.Code ? Runtime(key) : null;
        return new CellView
        {
            Id = key,
            Kind = cell.Kind.ToString().ToLowerInvariant(),
            // An app's viewers see outputs, not code.
            Source = options.AppMode && cell.Kind != CellKind.Markdown ? "" : cell.Source,
            Attrs = new CellAttrs { HideCode = cell.HideCode, Disabled = cell.Disabled, Name = cell.Name },
            Status = r?.Status ?? "idle",
            ElapsedMs = r?.ElapsedMs,
            Message = r?.Message,
            Outputs = r?.Outputs ?? [],
            Diagnostics = options.AppMode ? [] : DiagnosticsOf(key),
            Runs = r?.Runs ?? 0,
            Edited = IsEdited(cell),
        };
    }

    // ---- the file on disk ---------------------------------------------------------------------------

    void SaveNow()
    {
        saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        if (options.AppMode) return;
        var text = doc.Serialize();
        try
        {
            if (text != diskText)
            {
                NotebookFile.WriteTextAtomic(Path, text);
                diskText = text;
                watcher.Saw();
            }
            // Each tab learns which of its edits are now on disk.
            foreach (var c in clients) c.Send(new ServerMessage { Type = BrowserMessages.FileSaved, NotebookVersion = version, Ack = c.LastSeq });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Often another program has the file open for a moment (common on Windows). Try again shortly.
            saveFailures++;
            if (saveFailures == 3)
                Broadcast(Error($"Couldn't save {System.IO.Path.GetFileName(Path)}: {e.Message} Your changes are kept in the editor and Reverb keeps trying."));
            saveTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
            return;
        }
        saveFailures = 0;
    }

    async Task CheckFileAsync()
    {
        string text;
        try
        {
            text = await ReadWithRetriesAsync();
        }
        catch (FileNotFoundException)
        {
            return; // deleted or mid-rename; the next save writes it again
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Broadcast(Error($"{System.IO.Path.GetFileName(Path)} changed on disk but couldn't be read: {e.Message}"));
            return;
        }
        if (text == diskText) return;

        diskText = text;
        doc = Load(text, options.RuntimeVersion);
        saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        foreach (var gone in runtime.Keys.Where(k => doc.FindCell(k) is null).ToList()) runtime.Remove(gone);
        version++;
        SyncLazy();
        SubmitAll();
        SendUpdate();
        Analyze();
        BroadcastSnapshot();
        Broadcast(new ServerMessage { Type = BrowserMessages.FileExternalChange, NotebookVersion = version, Message = $"{System.IO.Path.GetFileName(Path)} changed on disk. The notebook was reloaded." });
    }

    async Task<string> ReadWithRetriesAsync()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return NotebookFile.ReadText(Path);
            }
            catch (IOException) when (attempt < 5 && File.Exists(Path))
            {
                await Task.Delay(50); // another program is still writing it
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await InvokeAsync(async () =>
        {
            disposed = true;
            watcher.Dispose();
            SaveNow();
            kernelGeneration++;
            if (kernel is not null) await kernel.DisposeAsync();
            kernel = null;
        });
        inbox.Writer.TryComplete();
        await loop;
        await saveTimer.DisposeAsync();
    }
}
