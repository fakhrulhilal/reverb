using System.Text;
using System.Text.Json;
using Reverb.Kernel;

namespace Reverb.Cli;

/// <summary>
/// <c>reverb exec</c>: runs every cell in a kernel process and prints what the notebook prints, the
/// way <c>dotnet run</c> would. Errors go to stderr; the exit code is 1 if any cell failed.
/// </summary>
static class ExecCommand
{
    sealed class CellResult
    {
        public string Status = "stale";
        public string? Message;
        public List<OutputData> Outputs = [];
    }

    public static async Task<int> RunAsync(string[] args)
    {
        string? path = null;
        var json = false;
        var notebookArgs = Array.Empty<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--") { notebookArgs = args[(i + 1)..]; break; }
            if (args[i] == "--json") json = true;
            else if (path is null && !args[i].StartsWith('-')) path = args[i];
            else
            {
                Console.Error.WriteLine($"error: unexpected argument '{args[i]}'. Usage: reverb exec <file.cs> [--json] [-- args]");
                return 2;
            }
        }
        if (path is null)
        {
            Console.Error.WriteLine("error: no notebook given. Usage: reverb exec <file.cs> [--json] [-- args]");
            return 2;
        }

        var loaded = LoadedNotebook.Load(path, Console.Error);
        if (loaded is null) return 2;
        foreach (var f in AmbientBuildFiles.Find(path))
            Console.Error.WriteLine($"warning: {f.Path}: {f.Why}");

        var results = new Dictionary<string, CellResult>(StringComparer.Ordinal);
        var order = loaded.Document.Cells.Where(c => c.Kind == Format.CellKind.Code && c.Key is not null).Select(c => c.Key!).ToList();
        List<DiagnosticData> diagnostics = [];
        CellResult Result(string cell) => results.TryGetValue(cell, out var r) ? r : results[cell] = new CellResult();

        // Kernel output can mix stdout text and displays; print in arrival order so it matches dotnet run.
        var stdout = Console.Out;
        var stderr = Console.Error;
        void OnEvent(KernelEvent e)
        {
            switch (e.Type)
            {
                case EventTypes.Diagnostics:
                    diagnostics = e.Diagnostics ?? [];
                    break;
                case EventTypes.Status when e.Cell is not null:
                    Result(e.Cell).Status = e.Status ?? "";
                    Result(e.Cell).Message = e.Message;
                    break;
                case EventTypes.Cleared when e.Cell is not null:
                    Result(e.Cell).Outputs.Clear();
                    break;
                case EventTypes.Stream when e.Cell is not null:
                    Result(e.Cell).Outputs.Add(new OutputData { Stream = e.Stream, Text = e.Text });
                    if (!json) (e.Stream == StreamItem.Stderr ? stderr : stdout).Write(e.Text);
                    break;
                case EventTypes.Display when e.Cell is not null && e.Output is not null:
                    Result(e.Cell).Outputs.Add(e.Output);
                    if (!json) PrintDisplay(e.Cell, e.Output, stdout, stderr);
                    break;
                case EventTypes.Outputs when e.Cell is not null:
                    Result(e.Cell).Outputs = e.Outputs ?? [];
                    break;
            }
        }

        KernelClient client;
        try
        {
            client = await KernelClient.StartAsync(new KernelLaunch
            {
                EntryAssembly = typeof(Program).Assembly.Location,
                NotebookPath = path,
                Directives = loaded.Document.Preamble.Directives,
            });
        }
        catch (KernelExitedException e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 2;
        }

        var failed = false;
        await using (client)
        {
            client.Event += OnEvent;
            client.StrayOutput += line => { if (!json) stdout.WriteLine(line); };
            try
            {
                await client.SendAsync(new KernelRequest
                {
                    Type = RequestTypes.Open,
                    Text = loaded.Document.Serialize(),
                    Path = Path.GetFullPath(path),
                    Args = notebookArgs,
                    Lazy = false,
                    Run = true,
                });
            }
            catch (KernelExitedException e)
            {
                Console.Error.WriteLine($"error: {e.Message}");
                failed = true;
            }
            client.Event -= OnEvent;
        }
        stdout.Flush();

        foreach (var cell in order)
        {
            var r = Result(cell);
            if (r.Status is "error" or "upstream-error" or "stale") failed = true;
            // Runtime errors were already printed with the output; this covers compile errors and blocked cells.
            if (!json && r.Status is "error" or "upstream-error" && r.Outputs.All(o => o.Data?.ContainsKey(Output.Error) != true))
            {
                var errors = diagnostics.Where(d => d.Cell == cell && d.Severity == "error").ToList();
                if (errors.Count == 0) stderr.WriteLine($"error in cell '{cell}': {r.Message}");
                foreach (var d in errors) stderr.WriteLine($"error in cell '{cell}', line {d.StartLine + 1}: {d.Id}: {d.Message}");
            }
        }

        if (json) WriteJson(stdout, path, order, Result, diagnostics, failed);
        return failed ? 1 : 0;
    }

    static void PrintDisplay(string cell, OutputData output, TextWriter stdout, TextWriter stderr)
    {
        var data = output.Data ?? [];
        if (data.ContainsKey(Output.Widget)) return; // dotnet run shows nothing for a widget
        var text = data.GetValueOrDefault(Output.PlainText) ?? "";
        if (data.ContainsKey(Output.Error))
        {
            stderr.WriteLine($"error in cell '{cell}': {text}");
            return;
        }
        if (output.Title is not null) stdout.WriteLine(ConsoleHost.TitleLine(output.Title));
        stdout.WriteLine(text);
    }

    static void WriteJson(TextWriter writer, string path, List<string> order, Func<string, CellResult> result, List<DiagnosticData> diagnostics, bool failed)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("notebook", path);
            w.WriteBoolean("ok", !failed);
            w.WriteStartArray("cells");
            foreach (var cell in order)
            {
                var r = result(cell);
                w.WriteStartObject();
                w.WriteString("id", cell);
                w.WriteString("status", r.Status);
                if (r.Message is not null) w.WriteString("message", r.Message);
                w.WriteStartArray("outputs");
                foreach (var o in r.Outputs)
                {
                    w.WriteStartObject();
                    if (o.Stream is not null)
                    {
                        w.WriteString("stream", o.Stream);
                        w.WriteString("text", o.Text);
                    }
                    else
                    {
                        if (o.Title is not null) w.WriteString("title", o.Title);
                        w.WriteStartObject("data");
                        foreach (var (mime, value) in o.Data ?? []) w.WriteString(mime, value);
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("diagnostics");
                foreach (var d in diagnostics.Where(d => d.Cell == cell)) WriteDiagnostic(w, d);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("notebookDiagnostics");
            foreach (var d in diagnostics.Where(d => d.Cell is null)) WriteDiagnostic(w, d);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        writer.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
    }

    static void WriteDiagnostic(Utf8JsonWriter w, DiagnosticData d)
    {
        w.WriteStartObject();
        w.WriteString("id", d.Id);
        w.WriteString("severity", d.Severity);
        w.WriteString("message", d.Message);
        w.WriteNumber("line", d.StartLine + 1);
        w.WriteNumber("column", d.StartColumn + 1);
        w.WriteEndObject();
    }
}
