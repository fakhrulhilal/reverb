using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Reverb.Format;

public sealed record ConvertOptions
{
    /// <summary>The Reverb.Runtime version the new notebook references.</summary>
    public string RuntimeVersion { get; init; } = "0.1.0";
}

/// <summary>The converted notebook, and what didn't come across cleanly ("cell 3: ...").</summary>
public sealed record ConversionResult(NotebookDocument Document, IReadOnlyList<string> Warnings);

/// <summary>The file can't be converted at all. The message says why.</summary>
public sealed class NotebookConvertException(string message) : Exception(message);

/// <summary>
/// Converts .NET Interactive notebooks (Jupyter .ipynb and Polyglot .dib) into Reverb notebooks.
/// C# cells come across with their #r nuget references as #:package lines, usings in the setup
/// cell and types in the types cell. A trailing expression, which the REPL displays, is dumped.
/// Cells in other languages become Markdown notes. Outputs are dropped. Syntax only.
/// </summary>
public static partial class NotebookConverter
{
    sealed record SourceCell(int Number, string Kind, string Language, string Source);

    // Kernel names .NET Interactive uses, and how to call them in a warning.
    static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] = "C#", ["c#"] = "C#",
        ["fsharp"] = "F#", ["f#"] = "F#",
        ["pwsh"] = "PowerShell", ["powershell"] = "PowerShell",
        ["javascript"] = "JavaScript", ["js"] = "JavaScript",
        ["html"] = "HTML", ["sql"] = "SQL", ["kql"] = "KQL", ["mermaid"] = "Mermaid",
        ["http"] = "HTTP", ["python"] = "Python", ["value"] = "value",
        ["markdown"] = "Markdown", ["md"] = "Markdown", ["meta"] = "meta",
    };

    static string Canonical(string language) =>
        Languages.TryGetValue(language, out var name) ? name switch { "C#" => "csharp", "Markdown" => "markdown", _ => language.ToLowerInvariant() } : language.ToLowerInvariant();

    // ---- reading the formats ---------------------------------------------------------------------

    public static ConversionResult FromJupyter(string json, ConvertOptions options)
    {
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new NotebookConvertException($"The file isn't valid JSON, so it can't be a Jupyter notebook: {e.Message}");
        }
        using var _ = parsed;
        var root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array)
            throw new NotebookConvertException("The file has no \"cells\" list, so it isn't a Jupyter notebook (nbformat 4).");

        var defaultLanguage = "csharp";
        if (root.TryGetProperty("metadata", out var meta))
        {
            if (Text(meta, "polyglot_notebook", "kernelInfo", "defaultKernelName") is { } kernel) defaultLanguage = Canonical(kernel);
            else if (Text(meta, "kernelspec", "language") is { } language) defaultLanguage = Canonical(language);
            else if (Text(meta, "kernelspec", "name") is { } name) defaultLanguage = name.Contains("fsharp", StringComparison.OrdinalIgnoreCase) ? "fsharp" : name.Contains("csharp", StringComparison.OrdinalIgnoreCase) ? "csharp" : Canonical(name);
        }

        var list = new List<SourceCell>();
        var number = 0;
        foreach (var cell in cells.EnumerateArray())
        {
            number++;
            var kind = Text(cell, "cell_type") ?? "code";
            var source = cell.TryGetProperty("source", out var s)
                ? s.ValueKind == JsonValueKind.Array ? string.Concat(s.EnumerateArray().Select(l => l.GetString())) : s.GetString() ?? ""
                : "";
            var language = Text(cell, "metadata", "dotnet_interactive", "language") ?? Text(cell, "metadata", "polyglot_notebook", "kernelName") ?? defaultLanguage;
            list.Add(new SourceCell(number, kind, Canonical(language), source.ReplaceLineEndings("\n")));
        }
        if (defaultLanguage != "csharp" && !list.Any(c => c.Kind == "code" && Language(c) == "csharp"))
            throw new NotebookConvertException($"This is a {defaultLanguage} notebook, and reverb convert only converts C# notebooks.");
        return Convert(list, json, options);
    }

    [GeneratedRegex(@"^#!(?<name>[A-Za-z#][\w#-]*)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DibMarker();

    public static ConversionResult FromDib(string text, ConvertOptions options)
    {
        var defaultLanguage = "csharp";
        var list = new List<SourceCell>();
        string? language = null;
        var lines = new List<string>();
        var number = 0;

        void Flush()
        {
            if (language is null) return;
            var source = string.Join('\n', lines);
            if (language == "meta")
            {
                try
                {
                    using var doc = JsonDocument.Parse(source);
                    if (Text(doc.RootElement, "kernelInfo", "defaultKernelName") is { } k) defaultLanguage = Canonical(k);
                }
                catch (JsonException)
                {
                    // Metadata we can't read changes nothing.
                }
                return;
            }
            number++;
            list.Add(new SourceCell(number, language == "markdown" ? "markdown" : "code", language, source));
        }

        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var m = DibMarker().Match(line);
            // Only kernel names start a cell; #!time and other magics are part of the cell.
            if (m.Success && Languages.ContainsKey(m.Groups["name"].Value))
            {
                Flush();
                language = Canonical(m.Groups["name"].Value);
                lines.Clear();
                continue;
            }
            if (language is null && line.Trim().Length > 0) language = defaultLanguage; // code before any marker
            lines.Add(line);
        }
        Flush();
        return Convert(list, text, options);
    }

    static string? Text(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
        }
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    /// <summary>The cell's language, taking a "#!fsharp" first line into account.</summary>
    static string Language(SourceCell cell)
    {
        var first = cell.Source.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        var m = DibMarker().Match(first);
        return m.Success && Languages.ContainsKey(m.Groups["name"].Value) ? Canonical(m.Groups["name"].Value) : cell.Language;
    }

    static string WithoutLanguageLine(string source)
    {
        var lines = source.Split('\n').ToList();
        var first = lines.FindIndex(l => l.Trim().Length > 0);
        if (first >= 0 && DibMarker().Match(lines[first].Trim()) is { Success: true } m && Languages.ContainsKey(m.Groups["name"].Value)) lines.RemoveAt(first);
        return string.Join('\n', lines);
    }

    // ---- building the notebook -------------------------------------------------------------------

    sealed class Builder
    {
        public readonly List<string> Warnings = [];
        public readonly List<NotebookPackage> Packages = [];
        public readonly List<string> Usings = [];
        public readonly List<string> Types = [];
        public readonly List<(string Kind, string Source)> Cells = [];
        public readonly Dictionary<string, List<int>> Declared = new(StringComparer.Ordinal);
    }

    static ConversionResult Convert(List<SourceCell> cells, string input, ConvertOptions options)
    {
        var b = new Builder();
        foreach (var cell in cells)
        {
            if (cell.Kind == "markdown")
            {
                var text = NotebookDocument.NormalizeSource(cell.Source);
                if (text.Length > 0) b.Cells.Add(("markdown", text));
                continue;
            }
            var language = cell.Kind == "code" ? Language(cell) : "raw";
            var source = WithoutLanguageLine(cell.Source);
            if (NotebookDocument.NormalizeSource(source).Length == 0) continue;
            if (language == "csharp")
            {
                CSharpCell(b, cell.Number, source);
                continue;
            }
            var name = Languages.TryGetValue(language, out var n) ? n : language;
            var fence = language == "raw" ? "" : language;
            b.Cells.Add(("markdown",
                $"*This {(language == "raw" ? "raw" : name)} cell from the original notebook wasn't converted: Reverb notebooks are C# only.*\n\n```{fence}\n{NotebookDocument.NormalizeSource(source)}\n```"));
            b.Warnings.Add(language == "raw"
                ? $"cell {cell.Number}: a raw cell was kept as a Markdown note."
                : $"cell {cell.Number}: {name} isn't supported, so the cell was kept as a Markdown note. Rewrite it in C# or delete it.");
        }

        foreach (var (name, numbers) in b.Declared.Where(d => d.Value.Count > 1))
        {
            var where = numbers.Count == 2 ? $"cells {numbers[0]} and {numbers[1]}" : $"cells {string.Join(", ", numbers.SkipLast(1))} and {numbers[^1]}";
            b.Warnings.Add($"'{name}' is declared in {where}. A notebook is one program, so each name can be declared once: rename the later ones.");
        }

        // Cell ids come from the input, so converting the same file again gives the same result.
        var random = new Random(StableHash(input));
        var taken = new HashSet<string>(StringComparer.Ordinal);
        string Id()
        {
            var id = CellIds.New(random, taken);
            taken.Add(id);
            return id;
        }

        var setup = new List<string> { "using Reverb;", "using static Reverb.Notebook;" };
        foreach (var u in b.Usings) if (!setup.Contains(u)) setup.Add(u);
        var header = $"#!/usr/bin/env dotnet\n#:package {NotebookUpgrader.RuntimePackage}@{options.RuntimeVersion}\n{NotebookHeader.CurrentLine}\n\n// %% setup\n{string.Join('\n', setup)}\n";
        var doc = NotebookDocument.Parse(header);
        foreach (var (kind, source) in b.Cells)
            doc = doc.InsertCell(doc.Cells.Length, kind == "markdown" ? NotebookCell.Markdown(Id(), source) : NotebookCell.Code(Id(), source));
        if (b.Types.Count > 0) doc = doc.InsertCell(doc.Cells.Length, NotebookCell.Types(string.Join("\n\n", b.Types)));
        doc = doc.WithPackages(b.Packages.DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase));
        return new ConversionResult(doc, b.Warnings);
    }

    static int StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text) hash = (hash ^ c) * 16777619u;
        return (int)(hash & 0x7FFFFFFF);
    }

    // ---- C# cells ------------------------------------------------------------------------------------

    [GeneratedRegex(@"^#r\s+""nuget:\s*(?<name>[^,""\s]+)\s*(,\s*(?<version>[^""\s]+))?\s*""\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NugetReference();

    static readonly CSharpParseOptions Script = new(LanguageVersion.Preview, kind: SourceCodeKind.Script);

    // Calls that return nothing: a trailing one just needs its semicolon, not a Dump().
    static readonly HashSet<string> VoidCalls = new(StringComparer.Ordinal)
    {
        "WriteLine", "Write", "Display", "Dump", "Md", "Html", "Add", "AddRange", "Clear", "Remove", "RemoveAt", "Insert", "Sort", "Reverse", "ForEach", "Wait", "Dispose",
    };

    static void CSharpCell(Builder b, int number, string source)
    {
        // Line-level directives first: #r, #i and #! magics aren't C#.
        var lines = new List<string>();
        foreach (var line in source.Split('\n'))
        {
            var t = line.Trim();
            if (NugetReference().Match(t) is { Success: true } m)
            {
                var version = m.Groups["version"].Success ? m.Groups["version"].Value : "*";
                if (!m.Groups["version"].Success)
                    b.Warnings.Add($"cell {number}: #r \"nuget: {m.Groups["name"].Value}\" has no version, so the newest is used (#:package {m.Groups["name"].Value}@*). Pin a version to get the same results every time.");
                b.Packages.Add(new NotebookPackage(m.Groups["name"].Value, version));
                continue;
            }
            if (t.StartsWith("#r ", StringComparison.Ordinal))
            {
                lines.Add("// " + t);
                b.Warnings.Add($"cell {number}: {t} references a file, which a notebook can't do. Use a NuGet package (#:package) instead, or #:project for a project.");
                continue;
            }
            if (t.StartsWith("#i ", StringComparison.Ordinal))
            {
                lines.Add("// " + t);
                b.Warnings.Add($"cell {number}: {t} adds a package source. Add it to a nuget.config next to the notebook instead.");
                continue;
            }
            if (t.StartsWith("#!", StringComparison.Ordinal))
            {
                lines.Add("// " + t);
                b.Warnings.Add($"cell {number}: the magic command {t.Split(' ')[0]} has no Reverb equivalent and was commented out.");
                continue;
            }
            lines.Add(line);
        }

        var text = string.Join('\n', lines);
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(text, Script).GetRoot();
        var edits = new List<TextChange>();

        foreach (var u in root.Usings)
        {
            var directive = u.ToString().Trim();
            if (!b.Usings.Contains(directive)) b.Usings.Add(directive);
            edits.Add(new TextChange(u.FullSpan, ""));
        }

        var statements = root.Members.OfType<GlobalStatementSyntax>().ToList();
        foreach (var member in root.Members)
        {
            if (member is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or BaseNamespaceDeclarationSyntax)
            {
                b.Types.Add(NotebookDocument.NormalizeSource(member.ToFullString()));
                edits.Add(new TextChange(member.FullSpan, ""));
            }
            // In a script, a top-level 'var x = ...;' parses as a field; in a statement position, as a local.
            var declaration = member switch
            {
                GlobalStatementSyntax { Statement: LocalDeclarationStatementSyntax local } => local.Declaration,
                FieldDeclarationSyntax field => field.Declaration,
                _ => null,
            };
            if (declaration is not null)
            {
                foreach (var v in declaration.Variables)
                {
                    if (!b.Declared.TryGetValue(v.Identifier.Text, out var where)) b.Declared[v.Identifier.Text] = where = [];
                    if (!where.Contains(number)) where.Add(number);
                }
            }
        }

        // display(x) is .NET Interactive's; Reverb's is Display(x). x.Display() becomes x.Dump().
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is IdentifierNameSyntax { Identifier.Text: "display" } id)
                edits.Add(new TextChange(id.Span, "Display"));
            else if (call.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Display" } access && call.ArgumentList.Arguments.Count == 0)
                edits.Add(new TextChange(access.Name.Span, "Dump"));
        }

        // A trailing expression with no semicolon is what the REPL displays.
        if (statements.LastOrDefault() is { Statement: ExpressionStatementSyntax { SemicolonToken.IsMissing: true } last })
        {
            var expression = last.Expression;
            if (ReturnsNothing(expression))
            {
                edits.Add(new TextChange(new TextSpan(expression.Span.End, 0), ";"));
            }
            else
            {
                var simple = expression is IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax or ElementAccessExpressionSyntax or ObjectCreationExpressionSyntax;
                // Dump() goes on the outside of whatever the expression became after the edits above.
                var inner = ApplyEdits(text, edits.Where(e => expression.Span.Contains(e.Span)).ToList(), expression.Span);
                edits.RemoveAll(e => expression.Span.Contains(e.Span));
                edits.Add(new TextChange(expression.Span, simple ? $"{inner}.Dump();" : $"({inner}).Dump();"));
            }
        }

        var converted = TidyBlankLines(NotebookDocument.NormalizeSource(ApplyEdits(text, edits, new TextSpan(0, text.Length))));
        if (converted.Length > 0) b.Cells.Add(("code", converted));
    }

    static bool ReturnsNothing(ExpressionSyntax e) => e switch
    {
        AssignmentExpressionSyntax or PostfixUnaryExpressionSyntax or PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PreIncrementExpression or (int)SyntaxKind.PreDecrementExpression } => true,
        AwaitExpressionSyntax => true,
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "Console" } } } => true,
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax m } => VoidCalls.Contains(m.Name.Identifier.Text),
        InvocationExpressionSyntax { Expression: IdentifierNameSyntax id } => VoidCalls.Contains(id.Identifier.Text) || id.Identifier.Text == "display",
        _ => false,
    };

    /// <summary>The text of <paramref name="range"/> with the edits inside it applied.</summary>
    static string ApplyEdits(string text, List<TextChange> edits, TextSpan range)
    {
        var result = text[range.Start..range.End];
        foreach (var e in edits.Where(e => range.Contains(e.Span)).OrderByDescending(e => e.Span.Start))
        {
            var start = e.Span.Start - range.Start;
            result = result[..start] + e.NewText + result[(start + e.Span.Length)..];
        }
        return result;
    }

    static string TidyBlankLines(string source)
    {
        var lines = new List<string>();
        foreach (var line in source.Split('\n'))
        {
            if (line.Trim().Length == 0 && lines.Count > 0 && lines[^1].Trim().Length == 0) continue;
            lines.Add(line.Trim().Length == 0 ? "" : line);
        }
        return string.Join('\n', lines);
    }
}
