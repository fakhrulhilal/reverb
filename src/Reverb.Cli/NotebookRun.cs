using Reverb.Kernel;

namespace Reverb.Cli;

/// <summary>
/// Runs a whole notebook once in a kernel process and keeps what each cell ended up with. Used by
/// <c>reverb exec</c> and <c>reverb export</c>.
/// </summary>
sealed class NotebookRun
{
    public sealed class CellResult
    {
        public string Status = "stale";
        public string? Message;
        public List<OutputData> Outputs = [];
    }

    readonly Dictionary<string, CellResult> cells = new(StringComparer.Ordinal);

    public List<DiagnosticData> Diagnostics { get; private set; } = [];

    /// <summary>Set when the kernel couldn't be started or stopped mid-run.</summary>
    public string? KernelFailure { get; private set; }

    /// <summary>False when the kernel never started (a package build failed, say).</summary>
    public bool Started { get; private set; }

    public CellResult this[string cell] => cells.TryGetValue(cell, out var r) ? r : cells[cell] = new CellResult();

    void Apply(KernelEvent e)
    {
        switch (e.Type)
        {
            case EventTypes.Diagnostics:
                Diagnostics = e.Diagnostics ?? [];
                break;
            case EventTypes.Status when e.Cell is not null:
                this[e.Cell].Status = e.Status ?? "";
                this[e.Cell].Message = e.Message;
                break;
            case EventTypes.Cleared when e.Cell is not null:
                this[e.Cell].Outputs.Clear();
                break;
            case EventTypes.Stream when e.Cell is not null:
                this[e.Cell].Outputs.Add(new OutputData { Stream = e.Stream, Text = e.Text });
                break;
            case EventTypes.Display when e.Cell is not null && e.Output is not null:
                this[e.Cell].Outputs.Add(e.Output);
                break;
            case EventTypes.Outputs when e.Cell is not null:
                this[e.Cell].Outputs = e.Outputs ?? [];
                break;
        }
    }

    /// <summary>
    /// Runs the notebook. <paramref name="observe"/> sees each kernel event after it's been recorded,
    /// and <paramref name="stray"/> gets lines the kernel process printed outside the protocol.
    /// </summary>
    public static async Task<NotebookRun> RunAsync(string path, LoadedNotebook loaded, string[] args, Action<KernelEvent>? observe = null, Action<string>? stray = null)
    {
        var run = new NotebookRun();
        KernelClient client;
        try
        {
            var build = await PackageKernels.EnsureAsync(path, loaded.Document.Preamble.Directives);
            client = await KernelClient.StartAsync(new KernelLaunch
            {
                EntryAssembly = typeof(Program).Assembly.Location,
                NotebookPath = path,
                Directives = loaded.Document.Preamble.Directives,
            }.WithBuild(build));
        }
        catch (Exception e) when (e is KernelExitedException or KernelBuildException)
        {
            run.KernelFailure = e.Message;
            return run;
        }

        run.Started = true;
        await using (client)
        {
            void OnEvent(KernelEvent e)
            {
                run.Apply(e);
                observe?.Invoke(e);
            }
            client.Event += OnEvent;
            if (stray is not null) client.StrayOutput += stray;
            try
            {
                await client.SendAsync(new KernelRequest
                {
                    Type = RequestTypes.Open,
                    Text = loaded.Document.Serialize(),
                    Path = Path.GetFullPath(path),
                    Args = args,
                    Lazy = false,
                    Run = true,
                });
            }
            catch (KernelExitedException e)
            {
                run.KernelFailure = e.Message;
            }
            client.Event -= OnEvent;
            // With tracing on, show how the kernel spent its time.
            if (Environment.GetEnvironmentVariable("REVERB_KERNEL_TRACE") is { Length: > 0 }) Console.Error.Write(client.StandardError);
        }
        return run;
    }
}
