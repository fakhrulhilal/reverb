using System.Text;
using Reverb.Format;

namespace Reverb.Analysis.Tests;

static class TestNotebooks
{
    public static readonly ReferenceSet References = ReferenceSet.FromCurrentProcess(typeof(Reverb.Ui).Assembly);

    public const string DefaultSetup = "using Reverb;\nusing static Reverb.Notebook;";

    /// <summary>A notebook with code cells c1..cN, an optional types cell and the default setup.</summary>
    public static string Text(string[] cells, string? types = null, string? setup = DefaultSetup, string preamble = "")
    {
        var sb = new StringBuilder();
        sb.Append(preamble);
        sb.Append("#:package Reverb.Runtime@0.1.0\n// reverb-notebook v1\n\n");
        if (setup is not null) sb.Append("// %% setup\n").Append(setup).Append("\n\n");
        for (var i = 0; i < cells.Length; i++) sb.Append($"// %% id=c{i + 1}\n").Append(cells[i]).Append("\n\n");
        if (types is not null) sb.Append("// %% types\n").Append(types).Append('\n');
        return sb.ToString();
    }

    public static NotebookAnalysis Analyze(string[] cells, string? types = null, string? setup = DefaultSetup) =>
        NotebookAnalysis.Create(NotebookDocument.Parse(Text(cells, types, setup)), References);

    public static NotebookAnalysis AnalyzeText(string text) =>
        NotebookAnalysis.Create(NotebookDocument.Parse(text), References);

    public static IEnumerable<string> Fixtures() =>
        Directory.EnumerateFiles(Testing.Snapshot.FixturesDirectory, "*.cs")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Order(StringComparer.Ordinal);

    public static string FixturePath(string name) => Path.Combine(Testing.Snapshot.FixturesDirectory, name + ".cs");
}
