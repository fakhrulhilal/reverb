using System.Text;

namespace Reverb.Testing;

/// <summary>Builds notebook text for tests: code cells c1..cN, optional types cell.</summary>
public static class NotebookText
{
    public const string DefaultSetup = "using Reverb;\nusing static Reverb.Notebook;";

    public static string Build(IReadOnlyList<string> cells, string? types = null, string? setup = DefaultSetup,
        string preamble = "", IReadOnlyList<string>? ids = null, IReadOnlyList<string>? attributes = null)
    {
        var sb = new StringBuilder();
        sb.Append(preamble);
        sb.Append("#:package Reverb.Runtime@0.1.0\n// reverb-notebook v1\n\n");
        if (setup is not null) sb.Append("// %% setup\n").Append(setup).Append("\n\n");
        for (var i = 0; i < cells.Count; i++)
        {
            var id = ids?[i] ?? $"c{i + 1}";
            var attrs = attributes?[i] is { Length: > 0 } a ? " " + a : "";
            sb.Append($"// %% id={id}{attrs}\n").Append(cells[i]).Append("\n\n");
        }
        if (types is not null) sb.Append("// %% types\n").Append(types).Append('\n');
        return sb.ToString();
    }
}
