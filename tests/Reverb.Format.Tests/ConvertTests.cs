using Reverb.Testing;

namespace Reverb.Format.Tests;

public class ConvertTests
{
    static readonly ConvertOptions Options = new() { RuntimeVersion = "0.1.0" };

    static string Import(string name) => File.ReadAllText(Path.Combine(Snapshot.FixturesDirectory, "import", name));

    static string Ipynb(params string[] cells) =>
        "{\"cells\": [" + string.Join(",", cells) + "], \"metadata\": {\"kernelspec\": {\"language\": \"C#\"}}, \"nbformat\": 4, \"nbformat_minor\": 2}";

    static string Code(string source, string? language = null) =>
        $"{{\"cell_type\": \"code\", \"metadata\": {(language is null ? "{}" : $"{{\"dotnet_interactive\": {{\"language\": \"{language}\"}}}}")}, \"outputs\": [], \"source\": {System.Text.Json.JsonSerializer.Serialize(source)}}}";

    static string Markdown(string source) => $"{{\"cell_type\": \"markdown\", \"metadata\": {{}}, \"source\": {System.Text.Json.JsonSerializer.Serialize(source)}}}";

    static IEnumerable<NotebookCell> CodeCells(NotebookDocument doc) => doc.Cells.Where(c => c.Kind == CellKind.Code);

    [Fact]
    public void A_jupyter_notebook_converts()
    {
        var result = NotebookConverter.FromJupyter(Import("orders.ipynb"), Options);
        Snapshot.Match(result.Document.Serialize(), "tests/fixtures/import/snapshots/orders.ipynb.cs");
        Snapshot.Match(string.Join("\n", result.Warnings) + "\n", "tests/fixtures/import/snapshots/orders.ipynb.warnings");
    }

    [Fact]
    public void A_polyglot_dib_file_converts()
    {
        var result = NotebookConverter.FromDib(Import("orders.dib"), Options);
        Snapshot.Match(result.Document.Serialize(), "tests/fixtures/import/snapshots/orders.dib.cs");
        Snapshot.Match(string.Join("\n", result.Warnings) + "\n", "tests/fixtures/import/snapshots/orders.dib.warnings");
    }

    [Fact]
    public void Converting_twice_gives_the_same_file()
    {
        var a = NotebookConverter.FromJupyter(Import("orders.ipynb"), Options).Document.Serialize();
        var b = NotebookConverter.FromJupyter(Import("orders.ipynb"), Options).Document.Serialize();
        Assert.Equal(a, b);
    }

    [Fact]
    public void Nuget_references_become_package_lines()
    {
        var doc = NotebookConverter.FromJupyter(Ipynb(Code("#r \"nuget: Humanizer, 2.14.1\"\n#r \"nuget:Dapper,2.1.66\"\nvar a = 1;")), Options).Document;
        Assert.Contains(new Directive("package", "Humanizer", "2.14.1"), doc.Preamble.Directives);
        Assert.Contains(new Directive("package", "Dapper", "2.1.66"), doc.Preamble.Directives);
        Assert.Equal("var a = 1;", Assert.Single(CodeCells(doc)).Source);
    }

    [Fact]
    public void A_nuget_reference_without_a_version_takes_the_latest_with_a_warning()
    {
        var result = NotebookConverter.FromJupyter(Ipynb(Code("#r \"nuget: Humanizer\"\nvar a = 1;")), Options);
        Assert.Contains(new Directive("package", "Humanizer", "*"), result.Document.Preamble.Directives);
        Assert.Contains(result.Warnings, w => w.Contains("Humanizer") && w.Contains("no version"));
    }

    [Fact]
    public void Usings_go_to_setup_once_and_types_to_the_types_cell()
    {
        var doc = NotebookConverter.FromJupyter(Ipynb(
            Code("using System.Text;\nusing Reverb;\nvar sb = new StringBuilder();"),
            Code("using System.Text;\nrecord P(int X);\nclass Q { }\nvar p = new P(1);")), Options).Document;
        Assert.Equal("using Reverb;\nusing static Reverb.Notebook;\nusing System.Text;", doc.SetupCell!.Source);
        Assert.Equal("record P(int X);\n\nclass Q { }", doc.TypesCell!.Source);
        Assert.Equal(["var sb = new StringBuilder();", "var p = new P(1);"], CodeCells(doc).Select(c => c.Source));
    }

    [Fact]
    public void A_trailing_expression_is_dumped_and_display_calls_are_renamed()
    {
        var doc = NotebookConverter.FromJupyter(Ipynb(
            Code("var a = 2;\na"),
            Code("var b = a * 3;\na + b"),
            Code("display(a);\nb.Display();\nConsole.WriteLine(a)")), Options).Document;
        Assert.Equal(["var a = 2;\na.Dump();", "var b = a * 3;\n(a + b).Dump();", "Display(a);\nb.Dump();\nConsole.WriteLine(a);"], CodeCells(doc).Select(c => c.Source));
    }

    [Fact]
    public void Other_languages_become_commented_markdown_with_a_warning()
    {
        var result = NotebookConverter.FromJupyter(Ipynb(Code("var a = 1;"), Code("let x = 1", "fsharp"), Code("#!pwsh\nGet-Date")), Options);
        var markdown = result.Document.Cells.Where(c => c.Kind == CellKind.Markdown).ToList();
        Assert.Equal(2, markdown.Count);
        Assert.Contains("```fsharp\nlet x = 1\n```", markdown[0].Source);
        Assert.Contains("```pwsh\nGet-Date\n```", markdown[1].Source);
        Assert.Contains(result.Warnings, w => w.StartsWith("cell 2:") && w.Contains("F#"));
        Assert.Contains(result.Warnings, w => w.StartsWith("cell 3:") && w.Contains("PowerShell"));
    }

    [Fact]
    public void Magic_commands_and_dll_references_are_commented_out_with_warnings()
    {
        var result = NotebookConverter.FromJupyter(Ipynb(Code("#!time\n#r \"lib/My.dll\"\n#i \"nuget: https://example.com/feed\"\nvar a = 1;")), Options);
        Assert.Equal("// #!time\n// #r \"lib/My.dll\"\n// #i \"nuget: https://example.com/feed\"\nvar a = 1;", Assert.Single(CodeCells(result.Document)).Source);
        Assert.Contains(result.Warnings, w => w.Contains("#!time"));
        Assert.Contains(result.Warnings, w => w.Contains("My.dll") && w.Contains("#:package"));
        Assert.Contains(result.Warnings, w => w.Contains("nuget.config"));
    }

    [Fact]
    public void A_variable_declared_in_two_cells_is_pointed_out()
    {
        var result = NotebookConverter.FromJupyter(Ipynb(Code("var total = 1;"), Markdown("text"), Code("var total = 2;")), Options);
        Assert.Contains(result.Warnings, w => w.Contains("'total'") && w.Contains("cells 1 and 3"));
    }

    [Fact]
    public void Markdown_and_empty_cells()
    {
        var doc = NotebookConverter.FromJupyter(Ipynb(Markdown("# Title\nSome *text*."), Code("   \n"), Code("var a = 1;")), Options).Document;
        Assert.Equal("# Title\nSome *text*.", doc.Cells.Single(c => c.Kind == CellKind.Markdown).Source);
        Assert.Single(CodeCells(doc));
    }

    [Fact]
    public void A_python_notebook_is_refused()
    {
        var json = "{\"cells\": [], \"metadata\": {\"kernelspec\": {\"language\": \"python\", \"name\": \"python3\"}}, \"nbformat\": 4}";
        var e = Assert.Throws<NotebookConvertException>(() => NotebookConverter.FromJupyter(json, Options));
        Assert.Contains("python", e.Message);
    }

    [Fact]
    public void Broken_json_is_refused_with_a_reason()
    {
        var e = Assert.Throws<NotebookConvertException>(() => NotebookConverter.FromJupyter("{ \"cells\": [", Options));
        Assert.Contains("isn't valid JSON", e.Message);
    }
}
