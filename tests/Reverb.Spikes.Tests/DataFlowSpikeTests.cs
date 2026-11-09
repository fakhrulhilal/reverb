namespace Reverb.Spikes.Tests;

// Spike: does SemanticModel.AnalyzeDataFlow over ranges of top-level statements give the
// inputs/outputs the dependency graph needs? Each case checks one cell (Cell, default: the last code cell).
public class DataFlowSpikeTests(ITestOutputHelper output)
{
    public sealed record Case(
        string[] Cells,
        string[]? In = null,
        string[]? Out = null,
        string[]? Writes = null,
        string[]? Deps = null,
        string? Types = null,
        string? Cell = null,
        bool CompileError = false);

    static readonly Dictionary<string, Case> Cases = new()
    {
        ["plain local"] = new(["var a = 1;", "var b = a + 1;"], In: ["a"], Out: ["b"], Deps: ["c1"]),
        ["several statements"] = new(["var a = 1;", "var b = a + 1;\nvar c = b * 2;\nConsole.WriteLine(c);"], In: ["a"], Out: ["b", "c"]),
        ["read only"] = new(["var a = 1;", "Console.WriteLine(a);"], In: ["a"], Out: []),
        ["pattern var in declaration"] = new(["object o = 5;", "var isInt = o is int n;"], In: ["o"], Out: ["isInt", "n"]),
        ["pattern var in if condition leaks"] = new(["object o = 5;", "if (o is int m) Console.WriteLine(m);"], In: ["o"], Out: ["m"]),
        ["guard pattern used later"] = new(["object o = 5;", "if (o is not int g) throw new Exception();", "var h = g + 1;"], In: ["g"], Out: ["h"], Deps: ["c2"]),
        ["guard pattern declaring cell"] = new(["object o = 5;", "if (o is not int g) throw new Exception();", "var h = g + 1;"], Cell: "c2", In: ["o"], Out: ["g"]),
        ["out var"] = new(["var ok = int.TryParse(\"12\", out var p);"], In: [], Out: ["ok", "p"]),
        ["out var inside lambda"] = new(["Func<string, int> parse = s => int.TryParse(s, out var pv) ? pv : 0;"], Out: ["parse"]),
        ["deconstruction declaration"] = new(["var (q, r) = (1, 2);"], Out: ["q", "r"]),
        ["deconstruction read"] = new(["var (q, r) = (1, 2);", "var s = q + r;"], In: ["q", "r"], Out: ["s"]),
        ["deconstruction mixed form"] = new(["(var x1, var y1) = (1, 2);"], Out: ["x1", "y1"]),
        ["deconstruction assigns foreign"] = new(["var a = 1;\nvar b = 2;", "(a, b) = (b, a);"], In: ["a", "b"], Writes: ["a", "b"], Out: []),
        ["using var"] = new(["using var ms = new MemoryStream();"], Out: ["ms"]),
        ["using statement"] = new(["using (var ms = new MemoryStream()) { }"], Out: []),
        ["foreach"] = new(["int[] xs = [1, 2, 3];", "var total = 0;\nforeach (var x in xs) total += x;"], In: ["xs"], Out: ["total"]),
        ["foreach writes foreign"] = new(["int[] xs = [1, 2, 3];\nvar sum = 0;", "foreach (var x in xs) sum += x;"], In: ["sum", "xs"], Writes: ["sum"], Out: []),
        ["foreach deconstruction"] = new(["var d = new Dictionary<string, int>();", "foreach (var (k1, v1) in d) { }"], In: ["d"], Out: []),
        ["for loop"] = new(["for (var i = 0; i < 3; i++) { }"], Out: []),
        ["while pattern"] = new(["object o = 5;", "while (o is int w) { break; }"], In: ["o"], Out: []),
        ["block"] = new(["{ var hidden = 1; }"], Out: []),
        ["switch statement"] = new(["object o = 5;", "switch (o) { case int sw: break; }"], In: ["o"], Out: []),
        ["switch expression"] = new(["object o = 5;", "var desc = o switch { int z => z, _ => 0 };"], In: ["o"], Out: ["desc"]),
        ["try catch"] = new(["try { } catch (Exception ex) { Console.WriteLine(ex); }"], Out: []),
        ["query expression"] = new(["int[] xs = [1, 2];", "var qq = from x in xs where x > 1 select x;"], In: ["xs"], Out: ["qq"]),
        ["lambda captures"] = new(["var a = 1;", "Func<int> f = () => a * 2;"], In: ["a"], Out: ["f"]),
        ["lambda parameters"] = new(["int[] xs = [1];", "var sq = xs.Select(x => x * x).ToArray();"], In: ["xs"], Out: ["sq"]),
        ["local function in same cell reads foreign"] = new(["var a = 1;", "int Local() => a;\nvar z = Local();"], In: ["a"], Out: ["Local", "z"]),
        ["local function captures"] = new(["var k = 3;", "int Twice(int v) => v * k;"], In: ["k"], Out: ["Twice"]),
        // Roslyn reports what the called local function captures (k) as read at the call site.
        ["call local function from other cell"] = new(["var k = 3;", "int Twice(int v) => v * k;", "var t = Twice(2);"], In: ["k"], Out: ["t"], Deps: ["c1", "c2"]),
        // Legal C#: local functions are in scope for the whole top level. File order is NOT always a topological order.
        ["static local function"] = new(["static int Sq(int v) => v * v;", "var y = Sq(4);"], In: [], Out: ["y"], Deps: ["c1"]),
        ["method group"] = new(["static int Inc(int v) => v + 1;", "Func<int, int> g = Inc;"], Out: ["g"], Deps: ["c1"]),
        ["local function declared in a later cell"] = new(["var e = Later();", "static int Later() => 1;"], Cell: "c1", Out: ["e"], Deps: ["c2"]),
        ["local function reads a later variable"] = new(["int Get() => late;", "var late = 5;", "var u = Get();"], CompileError: true),
        ["types cell dependency"] = new(["var o = new Order(1);"], Types: "record Order(int Id);", Out: ["o"], Deps: ["types"]),
        ["types through a variable"] = new(["var o = new Order(1);", "var id = o.Id;"], Types: "record Order(int Id);", In: ["o"], Out: ["id"], Deps: ["c1", "types"]),
        ["await"] = new(["var a = 1;", "var v = await Task.FromResult(a);"], In: ["a"], Out: ["v"]),
        ["args"] = new(["var n = args.Length;"], In: ["args"], Out: ["n"]),
        ["compound assignment"] = new(["var a = 1;", "a += 1;"], In: ["a"], Writes: ["a"], Out: []),
        ["increment"] = new(["var a = 1;", "a++;"], In: ["a"], Writes: ["a"], Out: []),
        ["ref argument"] = new(["var a = 1;", "static void Bump(ref int v) => v++;\nBump(ref a);"], In: ["a"], Writes: ["a"], Out: ["Bump"]),
        ["out to existing"] = new(["var a = 1;", "int.TryParse(\"3\", out a);"], Writes: ["a"], Out: []),
        ["declared without value"] = new(["int later;", "later = 5;"], Writes: ["later"], Out: [], CompileError: false),
        ["nameof is not a read"] = new(["var a = 1;", "var nm = nameof(a);"], In: [], Out: ["nm"], Deps: []),
    };

    public static TheoryData<string> CaseNames => new(Cases.Keys);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Analyze(string name)
    {
        var c = Cases[name];
        var nb = SpikeNotebook.Create(c.Cells, types: c.Types);
        foreach (var e in nb.Errors) output.WriteLine($"error: {e}");

        if (c.CompileError)
        {
            Assert.NotEmpty(nb.Errors);
            return;
        }
        Assert.Empty(nb.Errors);

        var cellId = c.Cell ?? $"c{c.Cells.Length}";
        var flow = nb.Analyze(cellId);
        output.WriteLine(flow.ToString());

        if (c.In is not null) Assert.Equal(c.In, flow.Inputs);
        if (c.Out is not null) Assert.Equal(c.Out, flow.Outputs);
        Assert.Equal(c.Writes ?? [], flow.ForeignWrites);
        if (c.Deps is not null) Assert.Equal(c.Deps, flow.DependsOn);
    }

    [Fact]
    public void Top_level_symbols_are_what_later_statements_can_see()
    {
        var nb = SpikeNotebook.Create([
            "var a = 1;",
            "object o = a;\nif (o is int m) { var inner = m; }",
            "while (o is int w) { break; }\nforeach (var x in new[] { 1 }) { }",
            "Func<int, int> f = y => y;\nint F() => a;",
        ]);
        Assert.Empty(nb.Errors);
        var names = nb.TopLevelSymbols().Select(s => s.Name).Order(StringComparer.Ordinal);
        Assert.Equal(["F", "a", "f", "m", "o"], names);
    }
}
