using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Reverb.Format;
using Reverb.Kernel;
using Reverb.Server;

namespace Reverb.Cli;

/// <summary>
/// <c>reverb export &lt;file.cs&gt; --html</c>: runs the notebook and writes one self-contained HTML page
/// of its outputs (charts, tables, Markdown), with the code in collapsible blocks. The page needs no
/// server and nothing from the network.
/// </summary>
static class ExportCommand
{
    const string Usage = "Usage: reverb export <file.cs> --html [-o out.html] [--no-code] [-- args]";

    public static async Task<int> RunAsync(string[] args)
    {
        string? path = null;
        string? output = null;
        var html = false;
        var showCode = true;
        var notebookArgs = Array.Empty<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--":
                    notebookArgs = args[(i + 1)..];
                    i = args.Length;
                    break;
                case "--html":
                    html = true;
                    break;
                case "--no-code":
                    showCode = false;
                    break;
                case "-o" or "--output" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case var a when !a.StartsWith('-') && path is null:
                    path = a;
                    break;
                default:
                    Console.Error.WriteLine($"error: unexpected argument '{args[i]}'. {Usage}");
                    return 2;
            }
        }
        if (path is null)
        {
            Console.Error.WriteLine($"error: no notebook given. {Usage}");
            return 2;
        }
        if (!html)
        {
            Console.Error.WriteLine($"error: say which format to export. HTML is the only one so far: reverb export {path} --html");
            return 2;
        }
        output ??= Path.ChangeExtension(path, ".html");
        if (Path.GetFullPath(output) == Path.GetFullPath(path))
        {
            Console.Error.WriteLine($"error: the export would overwrite the notebook itself. Pick another name with -o.");
            return 2;
        }

        var page = EmbeddedFrontend.ReadText("export/export.js");
        var style = EmbeddedFrontend.ReadText("export/export.css");
        if (page is null || style is null)
        {
            Console.Error.WriteLine("error: this build of Reverb doesn't include the export page. Build it with the frontend (npm run build in frontend/).");
            return 2;
        }

        var loaded = LoadedNotebook.Load(path, Console.Error);
        if (loaded is null) return 2;
        Console.Error.WriteLine($"Running {Path.GetFileName(path)}...");
        var run = await NotebookRun.RunAsync(path, loaded, notebookArgs, stray: line => Console.Error.WriteLine(line));
        if (!run.Started)
        {
            Console.Error.WriteLine($"error: {run.KernelFailure}");
            return 2;
        }

        var doc = loaded.Document;
        var json = NotebookJson(doc, run, Path.GetFileName(path), showCode);
        var charts = doc.Cells.Any(c => c.Key is { } key && run[key].Outputs.Any(o => o.Data?.ContainsKey(Output.VegaLite) == true));
        string? chartScript = null;
        if (charts && (chartScript = EmbeddedFrontend.ReadText("export-charts/charts.js")) is null)
        {
            Console.Error.WriteLine("error: this build of Reverb doesn't include the chart library for exports. Build it with the frontend (npm run build in frontend/).");
            return 2;
        }

        var text = new StringBuilder()
            .Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append($"<meta name=\"generator\" content=\"Reverb {Program.Version}\">\n")
            .Append($"<title>{System.Net.WebUtility.HtmlEncode(Title(doc, Path.GetFileName(path)))}</title>\n")
            .Append("<style>\n").Append(style).Append("\n</style>\n</head>\n<body>\n<div id=\"root\"></div>\n")
            .Append("<script type=\"application/json\" id=\"reverb-notebook\">").Append(json).Append("</script>\n");
        if (chartScript is not null) text.Append("<script>").Append(ScriptText(chartScript)).Append("</script>\n");
        text.Append("<script>").Append(ScriptText(page)).Append("</script>\n</body>\n</html>\n");

        try
        {
            NotebookFile.WriteTextAtomic(output, text.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: couldn't write {output}: {e.Message}");
            return 2;
        }

        var failed = doc.Cells.Where(c => c.Kind == CellKind.Code && c.Key is { } key && run[key].Status is "error" or "upstream-error").Select(c => c.Key!).ToList();
        var size = new FileInfo(output).Length;
        Console.Error.WriteLine($"Wrote {output} ({size / 1024:N0} KB).");
        if (run.KernelFailure is not null) Console.Error.WriteLine($"warning: the kernel stopped partway: {run.KernelFailure}");
        if (failed.Count > 0)
        {
            Console.Error.WriteLine($"warning: {failed.Count} cell{(failed.Count == 1 ? "" : "s")} failed ({string.Join(", ", failed)}); the page shows the errors. Run 'reverb exec {path}' to see them here.");
            return 1;
        }
        return run.KernelFailure is null ? 0 : 1;
    }

    /// <summary>The first Markdown heading, or the file name.</summary>
    static string Title(NotebookDocument doc, string name)
    {
        foreach (var cell in doc.Cells.Where(c => c.Kind == CellKind.Markdown))
        {
            var heading = cell.Source.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith('#'));
            if (heading is not null) return heading.TrimStart('#').Trim();
        }
        return name;
    }

    /// <summary>A script can't contain "&lt;/script" or the page would end it early.</summary>
    static string ScriptText(string js) => Regex.Replace(js, "</(script)", "<\\/$1", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static string NotebookJson(NotebookDocument doc, NotebookRun run, string name, bool showCode)
    {
        using var stream = new MemoryStream();
        // The default encoder escapes '<', '>' and '&', so the JSON can't end its script tag.
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("name", name);
            w.WriteString("reverbVersion", Program.Version);
            w.WriteStartArray("cells");
            foreach (var cell in doc.Cells)
            {
                if (cell.Key is not { } key) continue;
                if (cell.Kind is CellKind.Setup or CellKind.Types && !showCode) continue;
                w.WriteStartObject();
                w.WriteString("id", key);
                w.WriteString("kind", cell.Kind.ToString().ToLowerInvariant());
                if (cell.Kind == CellKind.Markdown || (showCode && !cell.HideCode)) w.WriteString("source", cell.Source);
                var r = cell.Kind == CellKind.Code ? run[key] : new NotebookRun.CellResult { Status = "idle" };
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
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
