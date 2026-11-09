using Microsoft.CodeAnalysis;

namespace Reverb.Analysis.Tests;

public class RuleTests(ITestOutputHelper output)
{
    public sealed record Case(string[] Cells, string? Types = null, string? Setup = TestNotebooks.DefaultSetup);

    /// <summary>Each case lists the Reverb diagnostics expected, as "cell id range".</summary>
    static readonly Dictionary<string, (Case Case, string[] Expected)> Positive = new()
    {
        ["RVB001 assignment"] = (new(["var a = 1;", "a = 2;"]), ["c2 RVB001 1:1-1:2"]),
        ["RVB001 compound"] = (new(["var a = 1;", "a += 2;"]), ["c2 RVB001 1:1-1:2"]),
        ["RVB001 increment"] = (new(["var a = 1;", "Console.WriteLine(a);\na++;"]), ["c2 RVB001 2:1-2:2"]),
        ["RVB001 deconstruction"] = (new(["var a = 1;\nvar b = 2;", "(a, b) = (b, a);"]), ["c2 RVB001 1:2-1:3", "c2 RVB001 1:5-1:6"]),
        ["RVB001 ref argument"] = (new(["var a = 1;", "static void Bump(ref int v) => v++;\nBump(ref a);"]), ["c2 RVB001 2:10-2:11"]),
        ["RVB001 out argument"] = (new(["var a = 1;", "int.TryParse(\"3\", out a);"]), ["c2 RVB001 1:23-1:24"]),
        ["RVB001 struct field"] = (new(["var p = new P();", "p.X = 3;"], Types: "struct P { public int X; }"), ["c2 RVB001 1:1-1:2"]),
        ["RVB002 anonymous output"] = (new(["var q = new[] { 1 }.Select(x => new { X = x }).ToList();", "var n = q.Count;"]),
            ["c1 RVB002 1:5-1:6", "c2 RVB002 1:9-1:10"]),
        ["RVB003 return"] = (new(["var a = 1;\nif (a > 0) return;"]), ["c1 RVB003 2:12-2:18"]),
        ["RVB003 goto other cell"] = (new(["start:\nConsole.WriteLine(1);", "goto start;"]), ["c2 RVB003 1:1-1:12"]),
        ["RVB004 type in code cell"] = (new(["var a = 1;\nrecord R(int X);"]), ["c1 RVB004 2:8-2:9"]),
        ["RVB004 using in code cell"] = (new(["var a = 1;", "using System.Text;"]), ["c2 RVB004 1:1-1:19"]),
        ["RVB004 statement in setup"] = (new(["var a = 1;"], Setup: "using Reverb;\nConsole.WriteLine(0);"), ["setup RVB004 2:1-2:22"]),
        ["RVB004 statement in types"] = (new(["var a = 1;"], Types: "record R;\nConsole.WriteLine(0);"), ["types RVB004 2:1-2:22"]),
        ["RVB005 Add"] = (new(["var list = new List<int>();", "list.Add(1);"]), ["c2 RVB005 1:1-1:12"]),
        ["RVB005 indexer"] = (new(["var d = new Dictionary<string, int>();", "d[\"a\"] = 1;"]), ["c2 RVB005 1:1-1:7"]),
        ["RVB005 array element"] = (new(["int[] arr = [1, 2];", "arr[0] = 5;"]), ["c2 RVB005 1:1-1:7"]),
        ["RVB005 property"] = (new(["var b = new Box();", "b.V = 3;"], Types: "class Box { public int V { get; set; } }"), ["c2 RVB005 1:1-1:4"]),
        ["RVB005 Array.Sort"] = (new(["int[] arr = [2, 1];", "Array.Sort(arr);"]), ["c2 RVB005 1:1-1:16"]),
        ["RVB005 StringBuilder"] = (new(["var sb = new System.Text.StringBuilder();", "sb.Append(\"x\");"]), ["c2 RVB005 1:1-1:15"]),
        ["RVB006 later local function"] = (new(["var e = Later();", "static int Later() => 1;"]), ["c1 RVB006 1:9-1:14"]),
        ["RVB007 span"] = (new(["int[] arr = [1, 2, 3];\nvar span = arr.AsSpan();", "var n = span.Length;"]),
            ["c1 RVB007 2:5-2:9", "c2 RVB007 1:9-1:13"]),
        ["RVB007 generic local function"] = (new(["static T Id<T>(T x) => x;", "var v = Id(1);"]), ["c1 RVB007 1:10-1:12", "c2 RVB007 1:9-1:11"]),
        ["RVB007 optional parameter"] = (new(["static int Add(int a, int b = 1) => a + b;", "var v = Add(1);"]), ["c1 RVB007 1:12-1:15", "c2 RVB007 1:9-1:12"]),
    };

    static readonly Dictionary<string, Case> Negative = new()
    {
        ["read only"] = new(["var a = 1;", "var b = a + 1;"]),
        ["reassign in own cell"] = new(["var a = 1;\na = 2;\na++;"]),
        ["shadowing lambda parameter"] = new(["var a = 1;", "Func<int, int> f = a2 => a2 + a;"]),
        ["anonymous type kept in cell"] = new(["var q = new { A = 1 };\nConsole.WriteLine(q.A);"]),
        ["named tuple shared"] = new(["var q = (A: 1, B: 2);", "var n = q.A;"]),
        ["record shared"] = new(["var o = new Order(1);", "var id = o.Id;"], Types: "record Order(int Id);"),
        ["return inside lambda and local function"] = new(["Func<int> f = () => { return 1; };\nint G() { return 2; }\nvar g = G();"]),
        ["goto in same cell"] = new(["var i = 0;\nagain:\ni++;\nif (i < 3) goto again;"]),
        ["LINQ append"] = new(["var list = new List<int>();", "var more = list.Append(1).ToList();"]),
        ["immutable add"] = new(["var im = System.Collections.Immutable.ImmutableList<int>.Empty;", "var im2 = im.Add(1);"]),
        ["string replace"] = new(["var s = \"ab\";", "var t = s.Replace(\"a\", \"b\");"]),
        ["mutation in own cell"] = new(["var list = new List<int>();\nlist.Add(1);\nlist[0] = 2;"]),
        ["mutating a local copy"] = new(["var list = new List<int> { 1 };", "var copy = list.ToList();\ncopy.Add(2);"]),
        ["earlier local function"] = new(["static int Earlier() => 1;", "var e = Earlier();"]),
        ["capturing local function"] = new(["var k = 3;", "int Twice(int v) => v * k;", "var t = Twice(2);"]),
        ["span inside one cell"] = new(["int[] arr = [1, 2, 3];\nvar span = arr.AsSpan();\nvar n = span.Length;"]),
        ["generic local function in one cell"] = new(["static T Id<T>(T x) => x;\nvar v = Id(1);"]),
        ["types and usings in place"] = new(["var r = new R(1);"], Types: "record R(int X);", Setup: "using System.Text;\nusing static System.Math;"),
    };

    public static TheoryData<string> PositiveNames => new(Positive.Keys);
    public static TheoryData<string> NegativeNames => new(Negative.Keys);

    [Theory]
    [MemberData(nameof(PositiveNames))]
    public void Reports(string name)
    {
        var (c, expected) = Positive[name];
        var a = TestNotebooks.Analyze(c.Cells, c.Types, c.Setup);
        foreach (var d in a.Diagnostics) output.WriteLine(d.ToString());
        var actual = a.Diagnostics.Where(d => d.Id.StartsWith("RVB", StringComparison.Ordinal))
            .Select(d => $"{d.CellKey} {d.Id} {d.Range}");
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(NegativeNames))]
    public void Does_not_report(string name)
    {
        var c = Negative[name];
        var a = TestNotebooks.Analyze(c.Cells, c.Types, c.Setup);
        foreach (var d in a.Diagnostics) output.WriteLine(d.ToString());
        Assert.Empty(a.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning));
    }

    [Fact]
    public void Rule_messages_name_the_cells_involved()
    {
        var a = TestNotebooks.Analyze(["var a = 1;", "a = 2;"]);
        var d = Assert.Single(a.Diagnostics, d => d.Id == RuleIds.AssignsOtherCellsVariable);
        Assert.Equal("This cell changes 'a', which is declared in cell 'c1'. A variable can only be set in the cell that declares it. Declare a new variable here instead, e.g. var a2 = ...;", d.Message);
    }

    [Fact]
    public void Bad_langversion_property_is_reported()
    {
        var a = TestNotebooks.AnalyzeText(TestNotebooks.Text(["var a = 1;"], preamble: "#:property LangVersion=banana\n"));
        var d = Assert.Single(a.Diagnostics, d => d.Id == "RVB100");
        Assert.Null(d.CellKey);
    }

    [Fact]
    public void Langversion_property_is_honoured()
    {
        // 'field' in a property accessor needs C# 14; with LangVersion=13 it's an ordinary identifier.
        var cells = new[] { "var b = new B();" };
        const string types = "class B { public int V { get => field; set => field = value; } }";
        Assert.Empty(TestNotebooks.AnalyzeText(TestNotebooks.Text(cells, types)).Diagnostics);
        var old = TestNotebooks.AnalyzeText(TestNotebooks.Text(cells, types, preamble: "#:property LangVersion=13\n"));
        Assert.Contains(old.Diagnostics, d => d.IsError && d.CellKey == "types");
    }
}
