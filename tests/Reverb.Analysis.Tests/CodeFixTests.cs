using Reverb.Format;

namespace Reverb.Analysis.Tests;

public class CodeFixTests
{
    static NotebookDiagnostic Only(NotebookAnalysis a, string id, string? cell = null) =>
        Assert.Single(a.Diagnostics, d => d.Id == id && (cell is null || d.CellKey == cell));

    /// <summary>Applies the fix for the one diagnostic with this id, and checks the result is clean of it.</summary>
    static NotebookDocument Fix(NotebookAnalysis a, string id, string? cell = null, string? title = null)
    {
        var d = Only(a, id, cell);
        Assert.Equal(title, NotebookCodeFixes.Title(a, d));
        var fixedDoc = NotebookCodeFixes.Apply(a, d);
        var after = NotebookAnalysis.Create(fixedDoc, TestNotebooks.References);
        Assert.DoesNotContain(after.Diagnostics, x => x.Id == id);
        Assert.False(after.HasErrors, string.Join("\n", after.Diagnostics));
        return fixedDoc;
    }

    static string Source(NotebookDocument doc, string key) => doc.FindCell(key)!.Source;

    // ---- RVB004 ------------------------------------------------------------------------------------

    [Fact]
    public void A_using_in_a_code_cell_moves_to_the_end_of_setup()
    {
        var a = TestNotebooks.Analyze(["var a = 1;", "using System.Text;\nvar sb = new StringBuilder();"]);
        var doc = Fix(a, RuleIds.MisplacedDeclaration, title: "Move to the setup cell");
        Assert.Equal("var sb = new StringBuilder();", Source(doc, "c2"));
        Assert.Equal(TestNotebooks.DefaultSetup + "\nusing System.Text;", doc.SetupCell!.Source);
    }

    [Fact]
    public void A_using_that_setup_already_has_is_just_removed()
    {
        var a = TestNotebooks.Analyze(["using System.Text;\nvar sb = new StringBuilder();"], setup: "using Reverb;\nusing System.Text;");
        var doc = Fix(a, RuleIds.MisplacedDeclaration, title: "Move to the setup cell");
        Assert.Equal("var sb = new StringBuilder();", Source(doc, "c1"));
        Assert.Equal("using Reverb;\nusing System.Text;", doc.SetupCell!.Source);
    }

    [Fact]
    public void A_type_in_a_code_cell_moves_to_the_types_cell()
    {
        var a = TestNotebooks.Analyze(["var p = new P(1);\n\n/// <summary>A point.</summary>\nrecord R(int X);"], types: "record P(int X);");
        var doc = Fix(a, RuleIds.MisplacedDeclaration, title: "Move to the types cell");
        Assert.Equal("var p = new P(1);", Source(doc, "c1"));
        Assert.Equal("record P(int X);\n/// <summary>A point.</summary>\nrecord R(int X);", doc.TypesCell!.Source);
    }

    [Fact]
    public void A_type_moves_into_a_new_types_cell_when_there_is_none()
    {
        var a = TestNotebooks.Analyze(["class Box\n{\n    public int V { get; set; }\n}\nvar b = new Box();"]);
        var doc = Fix(a, RuleIds.MisplacedDeclaration, title: "Move to the types cell");
        Assert.Equal("var b = new Box();", Source(doc, "c1"));
        Assert.Equal(CellKind.Types, doc.Cells[^1].Kind);
        Assert.Equal("class Box\n{\n    public int V { get; set; }\n}", doc.TypesCell!.Source);
    }

    [Fact]
    public void A_statement_in_setup_moves_to_a_new_code_cell_after_it()
    {
        var a = TestNotebooks.Analyze(["var a = 1;"], setup: "using Reverb;\nConsole.WriteLine(0);");
        var doc = Fix(a, RuleIds.MisplacedDeclaration, title: "Move to a new code cell");
        Assert.Equal("using Reverb;", doc.SetupCell!.Source);
        Assert.Equal(CellKind.Code, doc.Cells[1].Kind);
        Assert.Equal("Console.WriteLine(0);", doc.Cells[1].Source);
        Assert.Equal("c1", doc.Cells[2].Key);
    }

    [Fact]
    public void A_statement_in_types_moves_to_a_new_code_cell_before_it()
    {
        var a = TestNotebooks.Analyze(["var a = 1;"], types: "record R;\nConsole.WriteLine(0);");
        var doc = Fix(a, RuleIds.MisplacedDeclaration, title: "Move to a new code cell");
        Assert.Equal("record R;", doc.TypesCell!.Source);
        Assert.Equal("Console.WriteLine(0);", doc.Cells[^2].Source);
        Assert.Equal(CellKind.Code, doc.Cells[^2].Kind);
    }

    // ---- RVB002 ------------------------------------------------------------------------------------

    [Fact]
    public void An_anonymous_type_used_by_another_cell_becomes_a_record()
    {
        var a = TestNotebooks.Analyze(["var q = new[] { 1, 2 }.Select(x => new { X = x, Name = x.ToString() }).ToList();", "var n = q.Count;"]);
        // The reading cell has the error; the declaring cell has a warning. Either can be fixed.
        var title = "Use a record instead of the anonymous type";
        Assert.Equal(title, NotebookCodeFixes.Title(a, Only(a, RuleIds.AnonymousTypeShared, "c1")));
        var doc = Fix(a, RuleIds.AnonymousTypeShared, "c2", title);
        Assert.Equal("var q = new[] { 1, 2 }.Select(x => new QRow(x, x.ToString())).ToList();", Source(doc, "c1"));
        Assert.Equal("record QRow(int X, string Name);", doc.TypesCell!.Source);
    }

    [Fact]
    public void Inferred_member_names_and_an_existing_types_cell()
    {
        var a = TestNotebooks.Analyze(
            ["var orders = new[] { new Order(\"West\", 3) };",
             "var summary = orders.GroupBy(o => o.Region).Select(g => new { g.Key, Count = g.Count() }).ToList();",
             "Console.WriteLine(summary.Count);"],
            types: "record Order(string Region, int Total);");
        var doc = Fix(a, RuleIds.AnonymousTypeShared, "c3", "Use a record instead of the anonymous type");
        Assert.Equal("var summary = orders.GroupBy(o => o.Region).Select(g => new SummaryRow(g.Key, g.Count())).ToList();", Source(doc, "c2"));
        Assert.Equal("record Order(string Region, int Total);\nrecord SummaryRow(string Key, int Count);", doc.TypesCell!.Source);
    }

    [Fact]
    public void The_record_name_does_not_clash_with_an_existing_type()
    {
        var a = TestNotebooks.Analyze(["var q = new { A = 1 };", "var n = q.A;"], types: "record QRow;");
        var doc = Fix(a, RuleIds.AnonymousTypeShared, "c2", "Use a record instead of the anonymous type");
        Assert.Equal("var q = new QRow2(1);", Source(doc, "c1"));
        Assert.Equal("record QRow;\nrecord QRow2(int A);", doc.TypesCell!.Source);
    }

    [Fact]
    public void A_nested_anonymous_type_has_no_fix()
    {
        var a = TestNotebooks.Analyze(["var q = new { Inner = new { A = 1 } };", "var n = q.Inner.A;"]);
        var d = Only(a, RuleIds.AnonymousTypeShared, "c2");
        Assert.Null(NotebookCodeFixes.Title(a, d));
        var e = Assert.Throws<CodeFixException>(() => NotebookCodeFixes.Apply(a, d));
        Assert.Contains("anonymous", e.Message);
    }

    [Fact]
    public void Other_diagnostics_have_no_fix()
    {
        var a = TestNotebooks.Analyze(["var a = 1;", "a = 2;"]);
        Assert.Null(NotebookCodeFixes.Title(a, Only(a, RuleIds.AssignsOtherCellsVariable)));
    }
}
