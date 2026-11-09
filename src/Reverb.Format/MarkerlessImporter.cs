using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reverb.Format;

/// <summary>
/// Turns C# with no cell markers into cells: using directives become the setup cell, top-level
/// statements one code cell, and type declarations the types cell. Syntax only.
/// </summary>
internal static class MarkerlessImporter
{
    static readonly CSharpParseOptions Options = new CSharpParseOptions(LanguageVersion.Preview)
        .WithFeatures([new("FileBasedProgram", "true")]);

    public static IEnumerable<NotebookCell> Split(string text)
    {
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(text, Options).GetRoot();
        var members = root.Members;

        // Region boundaries in the text. Comments in front of a node travel with it.
        var setupEnd = root.Usings.Count > 0 || root.Externs.Count > 0
            ? Math.Max(root.Usings.LastOrDefault()?.FullSpan.End ?? 0, root.Externs.LastOrDefault()?.FullSpan.End ?? 0)
            : 0;

        var lastStatement = -1;
        for (var i = 0; i < members.Count; i++)
            if (members[i] is GlobalStatementSyntax) lastStatement = i;

        // Types start after the last statement. Anything declared before a statement is invalid
        // C# anyway (CS8803); it stays in the code cell so the compiler can report it there.
        var typesStart = lastStatement + 1 < members.Count ? members[lastStatement + 1].FullSpan.Start : text.Length;
        if (lastStatement < 0) typesStart = members.Count > 0 ? members[0].FullSpan.Start : text.Length;

        var setup = NotebookDocument.NormalizeSource(text[..setupEnd]);
        var code = NotebookDocument.NormalizeSource(text[setupEnd..typesStart]);
        var types = NotebookDocument.NormalizeSource(text[typesStart..]);

        if (setup.Length > 0) yield return NotebookCell.Setup(setup);
        if (code.Length > 0) yield return NotebookCell.Code(null, code);
        if (types.Length > 0) yield return NotebookCell.Types(types);
    }
}
