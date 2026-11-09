using System.Runtime.CompilerServices;

namespace Reverb.Spikes.Tests;

// Spike: compile two dependent cells, run them, edit the first, re-run both, and check
// that the replaced load context really unloads. Kept synchronous on purpose: async state
// machines in the test method could hold references and hide real leaks.
public class LoadContextSpikeTests(ITestOutputHelper output)
{
    static void RunAll(SpikeKernel k, SpikeNotebook nb, params string[] cellIds)
    {
        foreach (var id in cellIds) k.RunCellAsync(CellCodegen.Signature(nb, id)).GetAwaiter().GetResult();
    }

    [Fact]
    public void Edit_first_cell_rerun_both_and_old_context_unloads()
    {
        var k = new SpikeKernel();
        var v1 = SpikeNotebook.Create(["var a = 21;", "var b = a * 2;"]);
        Assert.Empty(v1.Errors);
        RunAll(k, v1, "c1", "c2");
        Assert.Equal(42, k.Store.Get<int>("b"));
        Assert.Equal(2, k.CompileCount);

        var v2 = SpikeNotebook.Create(["var a = 50;", "var b = a * 2;"]);
        RunAll(k, v2, "c1", "c2");
        Assert.Equal(100, k.Store.Get<int>("b"));
        // c1 recompiled; c2's source and signature are unchanged, so it's reused.
        Assert.Equal(3, k.CompileCount);
        Assert.Equal(4, k.RunCount);

        Assert.Single(k.Unloaded);
        Assert.True(Unload.AllCollected(k.Unloaded), "old context for c1 did not unload");
    }

    [Fact]
    public void Running_unchanged_cell_does_not_recompile()
    {
        var k = new SpikeKernel();
        var nb = SpikeNotebook.Create(["var a = 1;", "var b = a + 1;"]);
        RunAll(k, nb, "c1", "c2");
        RunAll(k, nb, "c1", "c2");
        RunAll(k, nb, "c2");
        Assert.Equal(2, k.CompileCount);
        Assert.Equal(5, k.RunCount);
    }

    [Fact]
    public void Closure_in_store_keeps_context_alive_until_replaced()
    {
        var k = new SpikeKernel();
        var v1 = SpikeNotebook.Create(["var a = 2;\nFunc<int, int> f = x => x * a;", "var y = f(5);"]);
        RunAll(k, v1, "c1", "c2");
        Assert.Equal(10, k.Store.Get<int>("y"));

        // Recompile c1 without running it: the old delegate is still in the store.
        var v2 = SpikeNotebook.Create(["var a = 3;\nFunc<int, int> f = x => x * a;", "var y = f(5);"]);
        k.Evict("c1");
        Assert.False(Unload.AllCollected(k.Unloaded, attempts: 5), "context should stay alive while its closure is in the store");

        RunAll(k, v2, "c1", "c2");
        Assert.Equal(15, k.Store.Get<int>("y"));
        Assert.True(Unload.AllCollected(k.Unloaded), "context did not unload after its closure was replaced");
    }

    [Fact]
    public void Types_cell_change_recompiles_cells_and_unloads_everything_old()
    {
        var k = new SpikeKernel();
        const string types1 = "record Order(int Id);";
        var v1 = SpikeNotebook.Create(["var o = new Order(7);", "var id = o.Id;"], types: types1);
        Assert.Empty(v1.Errors);
        k.SetTypes(types1);
        RunAll(k, v1, "c1", "c2");
        Assert.Equal(7, k.Store.Get<int>("id"));

        const string types2 = "record Order(int Id, string Name = \"x\");";
        var v2 = SpikeNotebook.Create(["var o = new Order(8);", "var id = o.Id;"], types: types2);
        k.SetTypes(types2);
        k.EvictAll();
        k.Store.Clear();
        RunAll(k, v2, "c1", "c2");
        Assert.Equal(8, k.Store.Get<int>("id"));
        Assert.Equal(4, k.CompileCount);

        // old types context + two old cell contexts
        Assert.Equal(3, k.Unloaded.Count);
        Assert.True(Unload.AllCollected(k.Unloaded), "old types or cell contexts did not unload");
    }

    [Fact]
    public void Exception_stack_trace_points_at_cell_and_line()
    {
        var k = new SpikeKernel();
        var nb = SpikeNotebook.Create(["var a = 1;", "var zero = a - 1;\n\nvar boom = a / zero;"]);
        RunAll(k, nb, "c1");
        var ex = Assert.Throws<DivideByZeroException>(() => RunAll(k, nb, "c2"));
        output.WriteLine(ex.StackTrace ?? "");
        Assert.Contains("cell:c2:line 3", ex.StackTrace);
    }

    [Fact]
    public void Compile_errors_in_generated_code_map_to_cell_lines()
    {
        // The analysis compilation would normally catch this; this checks the #line mapping only.
        var sig = new CellSignature("c9", "var x = 1;\nint y = \"nope\";", 1, [], []);
        var ex = Assert.Throws<CellCompileException>(() => new SpikeKernel().RunCellAsync(sig).GetAwaiter().GetResult());
        var error = ex.Diagnostics.First(d => d.Id == "CS0029");
        var span = error.Location.GetMappedLineSpan();
        Assert.Equal("cell:c9", span.Path);
        Assert.Equal(1, span.StartLinePosition.Line); // zero-based: line 2 of the cell
    }

    [Fact]
    public void Local_functions_cross_cells_as_delegates()
    {
        var k = new SpikeKernel();
        var nb = SpikeNotebook.Create(["var k2 = 3;", "int Twice(int v) => v * k2;\nstatic int Sq(int v) => v * v;", "var t = Twice(2) + Sq(3);"]);
        Assert.Empty(nb.Errors);
        RunAll(k, nb, "c1", "c2", "c3");
        Assert.Equal(15, k.Store.Get<int>("t"));
    }

    [Fact]
    public void Only_definitely_assigned_outputs_are_stored()
    {
        var nb = SpikeNotebook.Create(["object o = 5;", "if (o is int m) Console.WriteLine(m);\nif (o is not int g) throw new Exception();"]);
        var sig = CellCodegen.Signature(nb, "c2");
        Assert.Equal(["g"], sig.Outputs.Select(v => v.Name));
        var k = new SpikeKernel();
        RunAll(k, nb, "c1", "c2");
        Assert.Equal(5, k.Store.Get<int>("g"));
        Assert.False(k.Store.Contains("m"));
    }

    [Fact]
    public void Using_var_output_is_disposed_when_its_cell_returns()
    {
        // Under `dotnet run` the stream lives until the program ends, so c2 works there.
        // With one method per cell it's disposed when c1 returns. This is a real divergence
        // that has to be handled either with a diagnostic or by changing the codegen.
        var nb = SpikeNotebook.Create(["using var ms = new MemoryStream(new byte[4]);", "var len = ms.ReadByte();"]);
        Assert.Empty(nb.Errors);
        var k = new SpikeKernel();
        RunAll(k, nb, "c1");
        Assert.Throws<ObjectDisposedException>(() => RunAll(k, nb, "c2"));
    }

    [Fact]
    public void Generated_code_snapshot()
    {
        var nb = SpikeNotebook.Create(["var orders = new List<Order> { new(1, 5) };\nvar threshold = 2.0;",
            "var big = orders.Where(o => o.Total >= threshold).ToList();\nConsole.WriteLine(big.Count);"],
            types: "record Order(int Id, double Total);");
        var code = CellCodegen.Generate(CellCodegen.Signature(nb, "c2"), "using System.Text;");
        output.WriteLine(code);
        Assert.Contains("var orders = __ctx.Store.Get<global::System.Collections.Generic.List<global::Order>?>(\"orders\");", code);
        Assert.Contains("var threshold = __ctx.Store.Get<double>(\"threshold\");", code);
        Assert.Contains("#line 1 \"cell:c2\"", code);
        Assert.Contains("__ctx.Store.Set(\"big\", (global::System.Collections.Generic.List<global::Order>?)big);", code);
    }
}
