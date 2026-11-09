using Reverb.Format;

namespace Reverb.Analysis.Tests;

public class GraphTests
{
    static string Edges(NotebookAnalysis a) => a.Graph.ToString().TrimEnd();

    [Fact]
    public void Chain()
    {
        var a = TestNotebooks.Analyze(["var a = 1;", "var b = a + 1;", "var c = b * 2;", "var unrelated = 5;"]);
        Assert.False(a.HasErrors);
        Assert.Equal("c2 -> c1 [a]\nc3 -> c2 [b]", Edges(a));
        Assert.Equal(["c2", "c3"], a.Graph.Downstream("c1"));
        Assert.Equal(["c1", "c2", "c3"], a.Graph.RunSet("c1"));
        Assert.Empty(a.Graph.Downstream("c4"));
    }

    [Fact]
    public void Diamond_runs_each_cell_once_in_file_order()
    {
        var a = TestNotebooks.Analyze(["var a = 1;", "var b = a + 1;", "var c = a + 2;", "var d = b + c;"]);
        Assert.Equal("c2 -> c1 [a]\nc3 -> c1 [a]\nc4 -> c2 [b]\nc4 -> c3 [c]", Edges(a));
        Assert.Equal(["c2", "c3", "c4"], a.Graph.Downstream("c1"));
    }

    [Fact]
    public void Several_variables_from_one_cell_are_one_edge()
    {
        var a = TestNotebooks.Analyze(["var x = 1;\nvar y = 2;", "var z = x + y;"]);
        Assert.Equal("c2 -> c1 [x, y]", Edges(a));
    }

    [Fact]
    public void Widget_change_runs_readers_not_the_declaring_cell()
    {
        var a = TestNotebooks.Analyze([
            "var s = Ui.Slider(\"s\", value: 3);",
            "var x = s.Value * 2;",
            "var y = x + 1;",
            "var label = s.Label;\nvar z = 5;",
            "var w = z;",
        ]);
        Assert.Empty(a.Diagnostics);
        Assert.Equal(["c2", "c3", "c4", "c5"], a.Graph.ReadersOf("c1", "s"));
        Assert.Empty(a.Graph.ReadersOf("c4", "label"));
        Assert.Equal(["c5"], a.Graph.ReadersOf("c4", "z"));
    }

    [Fact]
    public void Setup_change_reruns_every_code_cell()
    {
        var a = TestNotebooks.Analyze(["var a = 1;", "var b = 2;"], types: "record R;");
        Assert.Equal(["c1", "c2"], a.Graph.Downstream("setup"));
    }

    [Fact]
    public void Types_cell_is_a_node()
    {
        var a = TestNotebooks.Analyze(["var o = new Order(1);", "var n = 3;", "var id = o.Id;"], types: "record Order(int Id);");
        Assert.Equal("c1 -> types []\nc3 -> c1 [o]\nc3 -> types []", Edges(a));
        Assert.Equal(["c1", "c3"], a.Graph.Downstream("types"));
    }

    [Fact]
    public void Types_used_only_through_member_access_count()
    {
        var a = TestNotebooks.Analyze(["var list = Make();\nstatic List<Order> Make() => [new(1)];", "var total = list.Sum(o => o.Id);"], types: "record Order(int Id);");
        Assert.True(a.Cell("c2")!.UsesTypes);
    }

    [Fact]
    public void Local_functions_are_dependencies()
    {
        var a = TestNotebooks.Analyze(["var k = 3;", "int Twice(int v) => v * k;", "var t = Twice(2);"]);
        Assert.Equal("c2 -> c1 [k]\nc3 -> c1 [k]\nc3 -> c2 [Twice]", Edges(a));
        var t = a.Cell("c3")!;
        Assert.Equal(["k: int", "Twice: Func<int, int>"], t.Inputs.Select(i => i.ToString()));
    }

    [Fact]
    public void Inputs_outputs_and_stored_values()
    {
        var a = TestNotebooks.Analyze([
            "object o = 5;\nvar orders = new List<Order> { new(1) };",
            "if (o is int m) Console.WriteLine(m);\nif (o is not int g) throw new Exception();\nvar big = orders.Where(x => x.Id > g).ToList();",
        ], types: "record Order(int Id);");
        Assert.Empty(a.Diagnostics);
        var c2 = a.Cell("c2")!;
        Assert.Equal(["o: object", "orders: List<Order>"], c2.Inputs.Select(i => i.ToString()));
        Assert.Equal(["big: List<Order>", "g: int", "m: int"], c2.Outputs.Select(i => i.ToString()));
        // m isn't definitely assigned when the cell ends, so it isn't stored.
        Assert.Equal(["big", "g"], c2.Stored.Select(i => i.Name));
        Assert.Equal("global::System.Collections.Generic.List<global::Order>?", c2.Inputs[1].GeneratedType);
    }

    [Fact]
    public void Args_is_an_input_without_a_cell()
    {
        var a = TestNotebooks.Analyze(["var n = args.Length;"]);
        var input = Assert.Single(a.Cell("c1")!.Inputs);
        Assert.Equal(VariableKind.Args, input.Kind);
        Assert.Null(input.DeclaringCell);
        Assert.Empty(a.Graph.Edges);
    }

    [Fact]
    public void Compiler_errors_are_mapped_to_cells()
    {
        var a = TestNotebooks.Analyze(["var a = 1;", "\nvar b = a +;", "var c = undefinedThing;"]);
        var syntax = Assert.Single(a.Diagnostics, d => d.CellKey == "c2");
        Assert.Equal("CS1525", syntax.Id);
        Assert.Equal(new CellRange(0, 11, 0, 12), syntax.Range);
        var name = Assert.Single(a.Diagnostics, d => d.CellKey == "c3");
        Assert.Equal("CS0103", name.Id);
        Assert.Equal("1:9-1:23", name.Range.ToString());
    }

    [Fact]
    public void Shebang_and_directives_are_not_errors()
    {
        var a = TestNotebooks.AnalyzeText(TestNotebooks.Text(["var a = 1;"], preamble: "#!/usr/bin/env dotnet\n#:property Nullable=enable\n"));
        Assert.False(a.HasErrors);
    }

    [Fact]
    public void Update_matches_a_fresh_analysis()
    {
        var doc = NotebookDocument.Parse(TestNotebooks.Text(["var a = 1;", "var b = a + 1;", "var c = 3;"]));
        var first = NotebookAnalysis.Create(doc, TestNotebooks.References);
        var edited = doc.WithCellSource("c3", "var c = b * 2;\nc = 1;");
        var updated = first.Update(edited);
        var fresh = NotebookAnalysis.Create(edited, TestNotebooks.References);
        Assert.Equal(fresh.Graph.ToString(), updated.Graph.ToString());
        Assert.Equal(fresh.Diagnostics.Select(d => d.ToString()), updated.Diagnostics.Select(d => d.ToString()));
        Assert.Equal(["c3"], updated.Graph.Downstream("c2"));
        // The edited notebook reuses the compilation's references.
        Assert.Same(first.Compilation.References.First(), updated.Compilation.References.First());
    }
}
