using Reverb.Format;

namespace Reverb.Cli;

/// <summary>
/// <c>reverb convert</c>: turns a .NET Interactive notebook (.ipynb or .dib) into a Reverb notebook.
/// What doesn't come across cleanly is listed as warnings, by cell.
/// </summary>
static class ConvertCommand
{
    const string Usage = "Usage: reverb convert <file.ipynb | file.dib> [-o out.cs] [--force]";

    public static int Run(string[] args)
    {
        string? path = null;
        string? output = null;
        var force = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" or "--output" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--force":
                    force = true;
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
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".ipynb" or ".dib"))
        {
            Console.Error.WriteLine($"error: {path} isn't a .ipynb or .dib file. reverb convert reads Jupyter and Polyglot (.NET Interactive) notebooks.");
            return 2;
        }
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"error: {path} doesn't exist.");
            return 2;
        }
        output ??= Path.ChangeExtension(path, ".cs");
        if (File.Exists(output) && !force)
        {
            Console.Error.WriteLine($"error: {output} already exists. Pick another name with -o, or add --force to replace it.");
            return 2;
        }

        ConversionResult result;
        try
        {
            var text = File.ReadAllText(path);
            var options = new ConvertOptions { RuntimeVersion = Program.RuntimeVersion };
            result = extension == ".ipynb" ? NotebookConverter.FromJupyter(text, options) : NotebookConverter.FromDib(text, options);
        }
        catch (NotebookConvertException e)
        {
            Console.Error.WriteLine($"error: {path}: {e.Message}");
            return 1;
        }

        try
        {
            NotebookFile.WriteTextAtomic(output, result.Document.Serialize());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: couldn't write {output}: {e.Message}");
            return 2;
        }

        foreach (var w in result.Warnings) Console.Error.WriteLine($"warning: {path}: {w}");
        var cells = result.Document.Cells.Count(c => c.Kind is CellKind.Code or CellKind.Markdown);
        Console.WriteLine($"Wrote {output} ({cells} cell{(cells == 1 ? "" : "s")}{(result.Warnings.Count > 0 ? $", {result.Warnings.Count} warning{(result.Warnings.Count == 1 ? "" : "s")}" : "")}).");
        Console.WriteLine($"Next: reverb check {output}, then reverb edit {output}.");
        return 0;
    }
}
