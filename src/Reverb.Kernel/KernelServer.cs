using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Reverb.Analysis;
using Reverb.Format;

namespace Reverb.Kernel;

/// <summary>
/// The kernel process's main loop: reads requests from stdin, runs them one at a time, and writes
/// events to stdout. Interrupts are handled as soon as they arrive, even while a cell runs.
/// </summary>
public static class KernelServer
{
    public static async Task<int> RunAsync(TextReader input, Stream output)
    {
        // Keep the real stdout for protocol messages; Console.Out is routed to cells from here on.
        var writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var events = new ProtocolEvents(writer);
        var session = new KernelSession(events);
        var queue = Channel.CreateUnbounded<KernelRequest>(new UnboundedChannelOptions { SingleReader = true });

        var processor = Task.Run(async () =>
        {
            await foreach (var request in queue.Reader.ReadAllAsync())
            {
                var ok = true;
                string? message = null;
                try
                {
                    ok = await HandleAsync(session, request);
                }
                catch (Exception e)
                {
                    ok = false;
                    message = $"{e.GetType().Name}: {e.Message}";
                    events.Send(new KernelEvent { Type = EventTypes.Error, Message = e.ToString() });
                }
                events.Send(new KernelEvent { Type = EventTypes.Done, RequestId = request.Id, Ok = ok, Message = message });
                if (request.Type == RequestTypes.Shutdown) break;
            }
        });

        // Analysis has its own worker so diagnostics keep coming while a cell runs.
        var analyses = Channel.CreateUnbounded<KernelRequest>(new UnboundedChannelOptions { SingleReader = true });
        var analyzer = Task.Run(() => AnalyzeLoopAsync(analyses.Reader, session.References, events));

        Warmup.Start();
        events.Send(new KernelEvent { Type = EventTypes.Ready });

        while (await input.ReadLineAsync() is { } line)
        {
            if (line.Length == 0) continue;
            KernelRequest? request;
            try
            {
                request = Protocol.DecodeRequest(line);
            }
            catch (JsonException e)
            {
                events.Send(new KernelEvent { Type = EventTypes.Error, Message = $"Couldn't read request: {e.Message}" });
                continue;
            }
            if (request is null) continue;

            if (request.Type == RequestTypes.Analyze)
            {
                await analyses.Writer.WriteAsync(request);
                continue;
            }
            if (request.Type == RequestTypes.Interrupt)
            {
                session.Interrupt();
                events.Send(new KernelEvent { Type = EventTypes.Done, RequestId = request.Id, Ok = true });
                continue;
            }
            // Work requests pre-empt the running cell now, not when the queue gets to them.
            if (request.Type is RequestTypes.Open or RequestTypes.Update or RequestTypes.Run or RequestTypes.RunAll or RequestTypes.SetWidget)
                session.CancelForNewWork();
            await queue.Writer.WriteAsync(request);
            if (request.Type == RequestTypes.Shutdown) break;
        }

        queue.Writer.TryComplete();
        analyses.Writer.TryComplete();
        await processor;
        await analyzer;
        await session.DisposeAsync();
        return 0;
    }

    static async Task<bool> HandleAsync(KernelSession session, KernelRequest r)
    {
        switch (r.Type)
        {
            case RequestTypes.Open:
                session.Lazy = r.Lazy ?? false;
                session.Args = r.Args ?? [];
                await session.OpenAsync(NotebookDocument.Parse(r.Text ?? ""), r.Run ?? true);
                return true;
            case RequestTypes.Update:
                await session.UpdateAsync(NotebookDocument.Parse(r.Text ?? ""), r.Cell, r.Run ?? false);
                return true;
            case RequestTypes.Run:
                await session.RunCellAsync(r.Cell ?? "");
                return true;
            case RequestTypes.RunAll:
                await session.RunAllAsync();
                return true;
            case RequestTypes.SetLazy:
                session.Lazy = r.Lazy ?? false;
                return true;
            case RequestTypes.SetWidget:
                return await session.SetWidgetAsync(r.WidgetId ?? "", WidgetValue(r.Value));
            case RequestTypes.Shutdown:
                return true;
            default:
                throw new InvalidOperationException($"Unknown request type '{r.Type}'.");
        }
    }

    /// <summary>
    /// Answers analyze requests. When several are waiting, only the newest is analysed; the others
    /// are answered as done without an analysis, since their text is already out of date.
    /// </summary>
    static async Task AnalyzeLoopAsync(ChannelReader<KernelRequest> reader, ReferenceSet references, ProtocolEvents events)
    {
        NotebookAnalysis? last = null;
        while (await reader.WaitToReadAsync())
        {
            var batch = new List<KernelRequest>();
            while (reader.TryRead(out var r)) batch.Add(r);
            foreach (var skipped in batch.SkipLast(1))
                events.Send(new KernelEvent { Type = EventTypes.Done, RequestId = skipped.Id, Ok = true });

            var request = batch[^1];
            try
            {
                var doc = NotebookDocument.Parse(request.Text ?? "");
                last = last is null ? NotebookAnalysis.Create(doc, references) : last.Update(doc);
                events.Send(new KernelEvent
                {
                    Type = EventTypes.Analysis,
                    RequestId = request.Id,
                    Version = request.Version,
                    Diagnostics = [.. last.Diagnostics.Select(ToData)],
                    Graph = [.. last.Graph.Edges.Select(e => new EdgeData { From = e.From, To = e.To, Via = [.. e.Via] })],
                });
                events.Send(new KernelEvent { Type = EventTypes.Done, RequestId = request.Id, Ok = true });
            }
            catch (Exception e)
            {
                last = null;
                events.Send(new KernelEvent { Type = EventTypes.Error, Message = e.ToString() });
                events.Send(new KernelEvent { Type = EventTypes.Done, RequestId = request.Id, Ok = false, Message = $"{e.GetType().Name}: {e.Message}" });
            }
        }
    }

    static object? WidgetValue(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.Number when value.Value.TryGetInt32(out var i) => i,
        JsonValueKind.Number => value.Value.GetDouble(),
        JsonValueKind.String => value.Value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    sealed class ProtocolEvents(StreamWriter writer) : IKernelEvents
    {
        readonly Lock gate = new();

        public void Send(KernelEvent e)
        {
            var line = Protocol.Encode(e);
            lock (gate) writer.WriteLine(line);
        }

        public void CellStatus(string cell, CellStatus status, long? elapsedMs, string? message) =>
            Send(new KernelEvent { Type = EventTypes.Status, Cell = cell, Status = StatusName(status), ElapsedMs = elapsedMs, Message = message });

        public void CellCleared(string cell) => Send(new KernelEvent { Type = EventTypes.Cleared, Cell = cell });

        public void CellStream(string cell, string stream, string text) =>
            Send(new KernelEvent { Type = EventTypes.Stream, Cell = cell, Stream = stream, Text = text });

        public void CellDisplay(string cell, Output output) =>
            Send(new KernelEvent { Type = EventTypes.Display, Cell = cell, Output = OutputData.From(output) });

        public void CellOutputs(string cell, IReadOnlyList<CellOutputItem> outputs) =>
            Send(new KernelEvent { Type = EventTypes.Outputs, Cell = cell, Outputs = [.. outputs.Select(OutputData.From)] });

        public void Diagnostics(NotebookAnalysis analysis) =>
            Send(new KernelEvent { Type = EventTypes.Diagnostics, Diagnostics = [.. analysis.Diagnostics.Select(ToData)] });
    }

    public static string StatusName(CellStatus status) => status switch
    {
        Kernel.CellStatus.Idle => "idle",
        Kernel.CellStatus.Queued => "queued",
        Kernel.CellStatus.Running => "running",
        Kernel.CellStatus.Stale => "stale",
        Kernel.CellStatus.Error => "error",
        Kernel.CellStatus.UpstreamError => "upstream-error",
        Kernel.CellStatus.Disabled => "disabled",
        _ => status.ToString().ToLowerInvariant(),
    };

    public static DiagnosticData ToData(NotebookDiagnostic d) => new()
    {
        Id = d.Id,
        Severity = d.Severity.ToString().ToLowerInvariant(),
        Message = d.Message,
        Cell = d.CellKey,
        StartLine = d.Range.StartLine,
        StartColumn = d.Range.StartColumn,
        EndLine = d.Range.EndLine,
        EndColumn = d.Range.EndColumn,
    };
}

/// <summary>
/// Pushes a small notebook through analysis, codegen, compile and run in the background so the
/// JIT and Roslyn are warm before the first real edit.
/// </summary>
static class Warmup
{
    public static void Start() => _ = Task.Run(async () =>
    {
        try
        {
            await using var session = new KernelSession();
            var text = "#:package Reverb.Runtime@0.1.0\n// reverb-notebook v1\n\n// %% setup\nusing Reverb;\n\n// %% id=a\nvar a = 1;\n\n// %% id=b\nvar b = a + 1;\n";
            var doc = NotebookDocument.Parse(text);
            await session.OpenAsync(doc);
            for (var i = 2; i < 12; i++) await session.UpdateAsync(doc.WithCellSource("a", $"var a = {i};"));
        }
        catch (Exception)
        {
            // Warm-up is best effort.
        }
    });
}
