using System.Text;
using Reverb.Format;
using Reverb.Testing;

namespace Reverb.Analysis.Tests;

public class FixtureTests(ITestOutputHelper output)
{
    public static TheoryData<string> Names => new(TestNotebooks.Fixtures());

    static NotebookAnalysis Load(string name) =>
        NotebookAnalysis.Create(NotebookFile.Load(TestNotebooks.FixturePath(name)), TestNotebooks.References);

    static string SnapshotPath(string name, string kind) => Path.Combine("tests", "fixtures", "snapshots", $"{name}.{kind}");

    [Theory]
    [MemberData(nameof(Names))]
    public void Graph(string name) => Snapshot.Match(DescribeGraph(Load(name)), SnapshotPath(name, "graph"));

    [Theory]
    [MemberData(nameof(Names))]
    public void Codegen(string name) => Snapshot.Match(DescribeCodegen(Load(name)), SnapshotPath(name, "codegen"));

    [Theory]
    [MemberData(nameof(Names))]
    public void Only_diagnostic_fixtures_have_errors(string name)
    {
        var a = Load(name);
        foreach (var d in a.Diagnostics) output.WriteLine(d.ToString());
        if (name.StartsWith("diag-", StringComparison.Ordinal)) Assert.NotEmpty(a.Diagnostics);
        else Assert.False(a.HasErrors);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Generated_code_compiles(string name)
    {
        var errors = CellCompiler.CompileAll(Load(name));
        foreach (var e in errors) output.WriteLine(e.ToString());
        Assert.Empty(errors);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Incremental_update_matches_fresh_analysis(string name)
    {
        // Re-analyse after a no-op edit of every cell, the path the editor takes.
        var a = Load(name);
        var doc = a.Document;
        foreach (var cell in doc.Cells.Where(c => c.Key is not null))
            doc = doc.WithCellSource(cell.Key!, cell.Source + "\n");
        var updated = a.Update(doc);
        Assert.Equal(DescribeGraph(a), DescribeGraph(updated));
    }

    internal static string DescribeGraph(NotebookAnalysis a)
    {
        var sb = new StringBuilder();
        sb.Append("cells:\n");
        foreach (var c in a.Cells.Where(c => c.Kind == CellKind.Code))
        {
            sb.Append($"  {c.Key}\n");
            if (c.Inputs.Length > 0) sb.Append($"    in:     {string.Join(", ", c.Inputs.Select(i => $"{i} <- {i.DeclaringCell ?? "(args)"}"))}\n");
            if (c.Outputs.Length > 0) sb.Append($"    out:    {string.Join(", ", c.Outputs)}\n");
            var notStored = c.Outputs.Select(o => o.Name).Except(c.Stored.Select(s => s.Name)).ToList();
            if (notStored.Count > 0) sb.Append($"    unstored: {string.Join(", ", notStored)}\n");
            if (c.UsesTypes) sb.Append("    uses types\n");
            if (c.UsingDeclarations.Length > 0) sb.Append($"    dispose later: {string.Join(", ", c.UsingDeclarations.SelectMany(u => u.Variables))}\n");
        }
        sb.Append("edges:\n");
        foreach (var e in a.Graph.Edges) sb.Append($"  {e.From} -> {e.To}{(e.Via.IsEmpty ? "" : $" [{string.Join(", ", e.Via)}]")}\n");
        sb.Append("downstream:\n");
        foreach (var key in a.Graph.Cells) sb.Append($"  {key}: {string.Join(", ", a.Graph.Downstream(key))}\n");
        sb.Append("diagnostics:").Append(a.Diagnostics.IsEmpty ? " none\n" : "\n");
        foreach (var d in a.Diagnostics) sb.Append($"  {d}\n");
        return sb.ToString();
    }

    static string DescribeCodegen(NotebookAnalysis a)
    {
        var sb = new StringBuilder();
        var types = CellCodeGenerator.GenerateTypes(a);
        if (types.Success) sb.Append("==== types ====\n").Append(types.Code!.Source);
        foreach (var c in a.Cells.Where(c => c.Kind == CellKind.Code))
        {
            sb.Append($"==== {c.Key} ====\n");
            var result = CellCodeGenerator.GenerateCell(a, c.Key);
            if (result.Success) sb.Append(result.Code!.Source);
            else foreach (var b in result.Blockers) sb.Append($"blocked: {b}\n");
        }
        return sb.ToString();
    }
}
