using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Reverb.Analysis;
using Reverb.Format;

namespace Reverb.Kernel;

public enum CellStatus
{
    Idle,
    Queued,
    Running,
    Stale,
    Error,
    UpstreamError,
    Disabled,
}

/// <summary>What the session tells the outside world. The kernel server turns these into protocol messages.</summary>
public interface IKernelEvents
{
    void CellStatus(string cell, CellStatus status, long? elapsedMs, string? message) { }
    void CellCleared(string cell) { }
    void CellStream(string cell, string stream, string text) { }
    void CellDisplay(string cell, Output output) { }
    void CellOutputs(string cell, IReadOnlyList<CellOutputItem> outputs) { }
    void Diagnostics(NotebookAnalysis analysis) { }
}

/// <summary>Counters for tests: exactly how much work an operation did.</summary>
public sealed class SessionStats
{
    public int Runs { get; internal set; }
    public int Compiles { get; internal set; }
    public List<string> RunLog { get; } = [];

    public void Reset()
    {
        Runs = 0;
        Compiles = 0;
        RunLog.Clear();
    }
}

/// <summary>
/// One open notebook inside the kernel: analysis, values, compiled cells, outputs and statuses.
/// Edits re-run exactly the affected cells, in file order. One run at a time; a new request
/// cancels the current run (cooperatively) and its unfinished cells are merged into the new work.
/// </summary>
public sealed class KernelSession : IAsyncDisposable
{
    sealed class CellState
    {
        public CellStatus Status = CellStatus.Stale;
        public string? Message;
        public List<CellOutputItem> Outputs = [];
        public List<object> Deferred = [];
        public List<Widget> Widgets = [];

        /// <summary>The code (or errors) this cell was last run with. A change means it must run again.</summary>
        public string? Signature;
    }

    readonly ReferenceSet references;
    readonly CellCompiler compiler;
    readonly Dictionary<string, CellState> states = new(StringComparer.Ordinal);
    readonly HashSet<string> pending = new(StringComparer.Ordinal);
    readonly Dictionary<string, (string Kind, object? Value)> widgetState = new(StringComparer.Ordinal);
    readonly Dictionary<string, (string Cell, string Variable, Widget Widget)> widgets = new(StringComparer.Ordinal);
    readonly SemaphoreSlim gate = new(1, 1);
    readonly Lock cancelGate = new();
    CancellationTokenSource? runCts;
    bool requeueOnCancel;
    CompileFailure? typesFailure;

    public KernelSession(IKernelEvents? events = null, ReferenceSet? references = null)
    {
        KernelHost.Install();
        Events = events ?? new NoEvents();
        this.references = references ?? DefaultReferences.Value;
        compiler = new CellCompiler(this.references);
    }

    static readonly Lazy<ReferenceSet> DefaultReferences = new(() => ReferenceSet.FromCurrentProcess(typeof(Ui).Assembly));

    sealed class NoEvents : IKernelEvents;

    public IKernelEvents Events { get; }
    public NotebookAnalysis? Analysis { get; private set; }
    public ValueStore Store { get; } = new();
    public SessionStats Stats { get; } = new();
    public bool Lazy { get; set; }
    public string[] Args { get; set; } = [];

    /// <summary>For tests that check old contexts unload.</summary>
    public CellCompiler Compiler => compiler;

    public CellStatus StatusOf(string cell) => states.TryGetValue(cell, out var s) ? s.Status : CellStatus.Stale;
    public string? MessageOf(string cell) => states.TryGetValue(cell, out var s) ? s.Message : null;
    public IReadOnlyList<CellOutputItem> OutputsOf(string cell) => states.TryGetValue(cell, out var s) ? s.Outputs : [];

    /// <summary>Widget values the user has set, by widget id. A fresh session can be seeded with these.</summary>
    public IReadOnlyDictionary<string, (string Kind, object? Value)> WidgetState => widgetState;

    public void SeedWidgetState(IEnumerable<KeyValuePair<string, (string Kind, object? Value)>> state)
    {
        foreach (var (id, v) in state) widgetState[id] = v;
    }

    // ---- requests ----------------------------------------------------------------------------

    /// <summary>Loads a notebook. Runs everything unless the session is lazy.</summary>
    public async Task OpenAsync(NotebookDocument doc, bool run = true)
    {
        CancelForNewWork();
        await gate.WaitAsync();
        try
        {
            Analysis = NotebookAnalysis.Create(doc, references);
            Events.Diagnostics(Analysis);
            foreach (var cell in CodeCells()) State(cell.Key).Status = CellStatus.Stale;
            foreach (var cell in CodeCells()) Events.CellStatus(cell.Key, CellStatus.Stale, null, null);
            if (run && !Lazy) pending.UnionWith(CodeCells().Select(c => c.Key));
            await DrainAsync();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Applies an edited version of the notebook and re-runs (or marks stale) what it affects.</summary>
    public async Task UpdateAsync(NotebookDocument doc)
    {
        CancelForNewWork();
        await gate.WaitAsync();
        try
        {
            if (Analysis is null)
            {
                Analysis = NotebookAnalysis.Create(doc, references);
                Events.Diagnostics(Analysis);
                pending.UnionWith(CodeCells().Select(c => c.Key));
            }
            else
            {
                var old = Analysis;
                Analysis = old.Update(doc);
                Events.Diagnostics(Analysis);
                Schedule(Affected(old, Analysis), explicitRun: false);
            }
            await DrainAsync();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Runs a cell and everything downstream of it (in lazy mode, just the cell; dependents go stale).</summary>
    public async Task RunCellAsync(string cell)
    {
        CancelForNewWork();
        await gate.WaitAsync();
        try
        {
            if (Analysis?.Cell(cell) is not { Kind: CellKind.Code }) return;
            if (Lazy)
            {
                pending.Add(cell);
                MarkStale(Analysis.Graph.Downstream(cell));
            }
            else
            {
                pending.UnionWith(Analysis.Graph.RunSet(cell));
            }
            await DrainAsync();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RunAllAsync()
    {
        CancelForNewWork();
        await gate.WaitAsync();
        try
        {
            if (Analysis is null) return;
            pending.UnionWith(CodeCells().Select(c => c.Key));
            await DrainAsync();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Sets a widget's value and re-runs the cells that read the variable holding it (not the
    /// cell that created it). Returns false if there's no such widget or the value doesn't fit.
    /// </summary>
    public async Task<bool> SetWidgetAsync(string widgetId, object? value)
    {
        CancelForNewWork();
        await gate.WaitAsync();
        try
        {
            if (!widgets.TryGetValue(widgetId, out var w) || !w.Widget.TrySetValue(value)) return false;
            widgetState[widgetId] = (w.Widget.Kind, w.Widget.BoxedValue);
            RedrawWidget(w.Cell, w.Widget);
            if (Analysis is not null)
                Schedule([.. Analysis.Graph.ReadersOf(w.Cell, w.Variable)], explicitRun: false);
            await DrainAsync();
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Asks the running cell to stop. Cells that were waiting to run are marked stale.</summary>
    public void Interrupt()
    {
        lock (cancelGate)
        {
            requeueOnCancel = false;
            runCts?.Cancel();
        }
    }

    /// <summary>
    /// Cancels the running cell (cooperatively) because new work is coming. Its unfinished cells are
    /// merged into that work. Called by request handlers, and by the server as soon as a request arrives.
    /// </summary>
    public void CancelForNewWork()
    {
        lock (cancelGate)
        {
            if (runCts is null) return;
            requeueOnCancel = true;
            runCts.Cancel();
        }
    }

    // ---- what to run --------------------------------------------------------------------------

    IEnumerable<CellAnalysis> CodeCells() => Analysis?.Cells.Where(c => c.Kind == CellKind.Code) ?? [];

    CellState State(string key)
    {
        if (!states.TryGetValue(key, out var s)) states[key] = s = new CellState();
        return s;
    }

    void Schedule(IReadOnlyCollection<string> cells, bool explicitRun)
    {
        if (Lazy && !explicitRun) MarkStale(cells);
        else pending.UnionWith(cells);
    }

    void MarkStale(IEnumerable<string> cells)
    {
        foreach (var key in cells)
        {
            var s = State(key);
            if (s.Status == CellStatus.Stale) continue;
            s.Status = CellStatus.Stale;
            Events.CellStatus(key, CellStatus.Stale, null, null);
        }
    }

    /// <summary>
    /// The cells an edit affects: every code cell whose generated code (or errors) differs from what
    /// it last ran with, plus everything downstream of those, plus the old dependents of deleted cells.
    /// </summary>
    HashSet<string> Affected(NotebookAnalysis old, NotebookAnalysis now)
    {
        var affected = new HashSet<string>(StringComparer.Ordinal);
        var nowKeys = now.Cells.Where(c => c.Kind == CellKind.Code).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var deleted in old.Cells.Where(c => c.Kind == CellKind.Code && !nowKeys.Contains(c.Key)))
        {
            ClearCell(deleted.Key);
            compiler.Evict(deleted.Key);
            states.Remove(deleted.Key);
            foreach (var w in widgets.Where(w => w.Value.Cell == deleted.Key).Select(w => w.Key).ToList()) widgets.Remove(w);
            affected.UnionWith(old.Graph.Downstream(deleted.Key).Where(nowKeys.Contains));
        }

        if (old.Document.TypesCell?.Source != now.Document.TypesCell?.Source)
            affected.UnionWith(now.Graph.Downstream("types"));

        foreach (var cell in now.Cells.Where(c => c.Kind == CellKind.Code))
        {
            if (Signature(now, cell) == State(cell.Key).Signature) continue;
            affected.UnionWith(now.Graph.RunSet(cell.Key));
        }
        return affected;
    }

    /// <summary>Identifies what a cell would run: its generated code or its errors, the types it uses, and its disabled flag.</summary>
    string Signature(NotebookAnalysis analysis, CellAnalysis cell)
    {
        var generated = CellCodeGenerator.GenerateCell(analysis, cell.Key);
        var sb = new StringBuilder();
        sb.Append(generated.Code?.Source ?? string.Join("\n", generated.Blockers));
        if (cell.UsesTypes) sb.Append('\n').Append(analysis.Document.TypesCell?.Source);
        sb.Append(analysis.Document.Cells[cell.Index].Disabled ? "\ndisabled" : "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..20];
    }

    // ---- running ------------------------------------------------------------------------------

    async Task DrainAsync()
    {
        while (pending.Count > 0 && Analysis is not null)
        {
            var analysis = Analysis;
            var order = CodeCells().Where(c => pending.Contains(c.Key)).Select(c => c.Key).ToList();
            pending.IntersectWith(order); // drop cells that no longer exist
            if (order.Count == 0) return;

            foreach (var key in order) SetStatus(key, CellStatus.Queued, null, null);

            compiler.EnsureTypes(analysis, out typesFailure);

            CancellationTokenSource cts;
            lock (cancelGate)
            {
                runCts = cts = new CancellationTokenSource();
                requeueOnCancel = false;
            }
            try
            {
                foreach (var key in order)
                {
                    if (cts.IsCancellationRequested) break;
                    var finished = await RunOneAsync(analysis, key, cts.Token);
                    if (finished) pending.Remove(key);
                    else break;
                }
            }
            finally
            {
                lock (cancelGate) runCts = null;
                Stats.Compiles = compiler.CompileCount;
            }

            if (cts.IsCancellationRequested)
            {
                bool requeue;
                lock (cancelGate) requeue = requeueOnCancel;
                if (requeue) return; // the request that cancelled us will drain the merged set
                MarkStale(pending);
                pending.Clear();
                return;
            }
        }
    }

    /// <summary>Runs one cell. Returns false if it was cancelled before it finished.</summary>
    async Task<bool> RunOneAsync(NotebookAnalysis analysis, string key, CancellationToken token)
    {
        var cell = analysis.Cell(key)!;
        var state = State(key);
        state.Signature = Signature(analysis, cell);
        ClearCell(key);

        if (analysis.Document.Cells[cell.Index].Disabled)
        {
            SetStatus(key, CellStatus.Disabled, null, "This cell is disabled.");
            return true;
        }

        var generated = CellCodeGenerator.GenerateCell(analysis, key);
        if (!generated.Success)
        {
            SetStatus(key, CellStatus.Error, null, string.Join("\n", generated.Blockers.Select(b => $"{b.Id}: {b.Message}")));
            return true;
        }

        // The cell's own errors come first: they don't depend on what ran before, while an upstream
        // cell's status can (a dependency on a later cell is always a compile error in this cell).
        foreach (var dep in analysis.Graph.DependenciesOf(key))
        {
            if (dep.To == "types")
            {
                if (typesFailure is not null || analysis.Diagnostics.Any(d => d.IsError && d.CellKey == "types"))
                {
                    SetStatus(key, CellStatus.UpstreamError, null, "The types cell has errors.");
                    return true;
                }
                continue;
            }
            var upstream = StatusOf(dep.To);
            if (upstream is CellStatus.Error or CellStatus.UpstreamError or CellStatus.Disabled)
            {
                var why = upstream == CellStatus.Disabled ? "is disabled" : "has errors";
                SetStatus(key, CellStatus.UpstreamError, null, $"Cell '{dep.To}' {why}, so this cell can't run.");
                return true;
            }
        }

        var run = compiler.GetCell(analysis, generated.Code!, cell.UsesTypes, out var failure);
        if (run is null)
        {
            SetStatus(key, CellStatus.Error, null, failure!.Message);
            return true;
        }

        var sink = new CellSink(key, this, token);
        var context = new CellContext(Store, Args, token);
        SetStatus(key, CellStatus.Running, null, null);
        Stats.Runs++;
        Stats.RunLog.Add(key);
        var clock = Stopwatch.StartNew();
        var previous = CellSink.Current;
        CellSink.Current = sink;
        Store.CurrentOwner = key;
        try
        {
            // Yield first so the cell starts on a fresh logical frame; its AsyncLocal sink flows with it.
            await Task.Run(() => run(context), CancellationToken.None);
            CellSink.Current = previous;
            sink.Flush();
            Finish(key, sink, context);
            SetStatus(key, CellStatus.Idle, clock.ElapsedMilliseconds, null);
            return true;
        }
        catch (Exception e) when (token.IsCancellationRequested && Unwrap(e) is OperationCanceledException)
        {
            CellSink.Current = previous;
            sink.Flush();
            Finish(key, sink, context);
            Store.RemoveOwnedBy(key);
            SetStatus(key, CellStatus.Stale, clock.ElapsedMilliseconds, "Interrupted.");
            return false;
        }
        catch (Exception e)
        {
            CellSink.Current = previous;
            var error = Unwrap(e);
            sink.Display(Formatters.FormatException(error, CellLocation(error)));
            sink.Flush();
            Finish(key, sink, context);
            Store.RemoveOwnedBy(key);
            SetStatus(key, CellStatus.Error, clock.ElapsedMilliseconds, $"{error.GetType().Name}: {error.Message}");
            return true;
        }
        finally
        {
            Store.CurrentOwner = "";
        }
    }

    void Finish(string key, CellSink sink, CellContext context)
    {
        var state = State(key);
        state.Outputs = sink.Items;
        state.Deferred = context.Deferred;
        state.Widgets = sink.Widgets;
        if (Analysis?.Cell(key) is not { } cell) return;
        // Remember which variable holds each widget, so a change knows whose readers to run.
        foreach (var output in cell.Stored)
        {
            if (Store.TryGet(output.Name, out var value) && value is Widget { Id: { } id } w && Store.OwnerOf(output.Name) == key)
                widgets[id] = (key, output.Name, w);
        }
    }

    /// <summary>Forgets a cell's last run: disposes deferred values, removes its variables and outputs.</summary>
    void ClearCell(string key)
    {
        if (!states.TryGetValue(key, out var state)) return;
        foreach (var d in Enumerable.Reverse(state.Deferred))
        {
            try
            {
                if (d is IAsyncDisposable ad) ad.DisposeAsync().AsTask().GetAwaiter().GetResult();
                else if (d is IDisposable dd) dd.Dispose();
            }
            catch (Exception)
            {
                // A failing Dispose shouldn't stop the cell from running again.
            }
        }
        state.Deferred = [];
        Store.RemoveOwnedBy(key);
        foreach (var id in widgets.Where(w => w.Value.Cell == key).Select(w => w.Key).ToList()) widgets.Remove(id);
        state.Widgets = [];
        if (state.Outputs.Count > 0)
        {
            state.Outputs = [];
            Events.CellCleared(key);
        }
    }

    /// <summary>The creating cell doesn't re-run when a widget changes, but its output should show the new value.</summary>
    void RedrawWidget(string cell, Widget widget)
    {
        var state = State(cell);
        var index = state.Outputs.FindIndex(o => o is DisplayItem d && ReferenceEquals(d.Widget, widget));
        if (index < 0) return;
        state.Outputs[index] = new DisplayItem(Formatters.Format(widget)) { Widget = widget };
        Events.CellOutputs(cell, state.Outputs);
    }

    internal void RestoreWidget(string id, Widget widget)
    {
        if (!widgetState.TryGetValue(id, out var saved)) return;
        // Keep the user's value only for the same kind of widget, and only if it still fits.
        if (saved.Kind != widget.Kind || !widget.TrySetValue(saved.Value)) widgetState.Remove(id);
    }

    void SetStatus(string key, CellStatus status, long? elapsed, string? message)
    {
        var state = State(key);
        state.Status = status;
        state.Message = message;
        Events.CellStatus(key, status, elapsed, message);
    }

    static Exception Unwrap(Exception e)
    {
        while (true)
        {
            switch (e)
            {
                case TargetInvocationException { InnerException: { } inner }: e = inner; continue;
                case AggregateException { InnerExceptions.Count: 1 } agg: e = agg.InnerExceptions[0]; continue;
                default: return e;
            }
        }
    }

    /// <summary>"cell 'summary', line 3" from the first stack frame in notebook code.</summary>
    static string? CellLocation(Exception e)
    {
        var frames = new StackTrace(e, fNeedFileInfo: true).GetFrames();
        foreach (var frame in frames)
        {
            var file = frame.GetFileName();
            if (file is null || !file.StartsWith("cell:", StringComparison.Ordinal)) continue;
            return $"cell '{file["cell:".Length..]}', line {frame.GetFileLineNumber()}";
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        Interrupt();
        await gate.WaitAsync();
        try
        {
            foreach (var key in states.Keys.ToList()) ClearCell(key);
            Store.Clear();
            compiler.Dispose();
        }
        finally
        {
            gate.Release();
        }
    }
}
