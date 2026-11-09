using System.Diagnostics;
using System.Text;

namespace Reverb.Spikes.Tests;

[CollectionDefinition("Latency", DisableParallelization = true)]
public class LatencyCollection;

// Warm edit-to-output latency for a trivial cell. Target: under 300 ms.
// Measures the in-process part of the kernel's work. JSON over stdio adds well under a
// millisecond per message and is not included.
[Collection("Latency")]
public class LatencySpikeTests(ITestOutputHelper output)
{
    const int Fillers = 18;

    static string BuildNotebook(int n)
    {
        var sb = new StringBuilder();
        sb.Append("// %% id=c1\nvar n = ").Append(n).Append(";\n");
        sb.Append("// %% id=c2\nvar doubled = n * 2;\n");
        for (var i = 0; i < Fillers; i++)
        {
            sb.Append($"// %% id=f{i}\n");
            sb.Append(i % 2 == 0
                ? $"var data{i} = Enumerable.Range(0, 100).Select(x => new Item(x, x * {i})).ToList();\n"
                : $"var sum{i} = data{i - 1}.Where(d => d.K % 3 == 0).Sum(d => d.V);\n");
        }
        sb.Append("// %% types\nrecord Item(int K, int V);\n");
        return sb.ToString();
    }

    sealed class Phases
    {
        public List<double> Parse = [], Diagnostics = [], Graph = [], CompileRun = [], Total = [];
    }

    Phases Measure(bool incremental, int warmup, int measured)
    {
        SpikeKernel.ReuseBaseCompilation = incremental;
        var kernel = new SpikeKernel();
        kernel.SetTypes("record Item(int K, int V);");
        var nb = SpikeNotebook.Create([]).WithText(BuildNotebook(0));
        Assert.Empty(nb.Errors);
        foreach (var cell in nb.Cells.Where(c => c.Id != "types"))
            kernel.RunCellAsync(CellCodegen.Signature(nb, cell.Id)).GetAwaiter().GetResult();

        var phases = new Phases();
        var sw = new Stopwatch();
        for (var i = 1; i <= warmup + measured; i++)
        {
            var text = BuildNotebook(i);
            var total = Stopwatch.StartNew();

            sw.Restart();
            nb = incremental ? nb.WithText(text) : FreshNotebook(text);
            var parse = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            var errors = nb.Errors;
            var diagnostics = sw.Elapsed.TotalMilliseconds;

            // Full graph rebuild: signatures for every code cell.
            sw.Restart();
            var sigs = nb.Cells.Where(c => c.Id != "types").ToDictionary(c => c.Id, c => CellCodegen.Signature(nb, c.Id));
            var graph = sw.Elapsed.TotalMilliseconds;

            // Run the edited cell and its dependent; the dependent's compiled assembly is reused.
            sw.Restart();
            kernel.RunCellAsync(sigs["c1"]).GetAwaiter().GetResult();
            kernel.RunCellAsync(sigs["c2"]).GetAwaiter().GetResult();
            var compileRun = sw.Elapsed.TotalMilliseconds;
            total.Stop();

            Assert.Empty(errors);
            Assert.Equal(i * 2, kernel.Store.Get<int>("doubled"));
            if (i <= warmup) continue;
            phases.Parse.Add(parse);
            phases.Diagnostics.Add(diagnostics);
            phases.Graph.Add(graph);
            phases.CompileRun.Add(compileRun);
            phases.Total.Add(total.Elapsed.TotalMilliseconds);
        }
        kernel.EvictAll();
        SpikeKernel.ReuseBaseCompilation = true;
        return phases;
    }

    static SpikeNotebook FreshNotebook(string text) => new SpikeNotebookFactory(text).Build();

    void Report(string label, Phases p)
    {
        static string Stats(List<double> xs)
        {
            var s = xs.Order().ToList();
            double At(double q) => s[(int)Math.Min(s.Count - 1, Math.Round(q * (s.Count - 1)))];
            return $"p50 {At(0.5),6:F1}  p90 {At(0.9),6:F1}  max {s[^1],6:F1} ms";
        }
        output.WriteLine($"[{label}] {p.Total.Count} edits, {Environment.ProcessorCount} cores, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        output.WriteLine($"  parse+replace tree   {Stats(p.Parse)}");
        output.WriteLine($"  analysis diagnostics {Stats(p.Diagnostics)}");
        output.WriteLine($"  dependency graph     {Stats(p.Graph)}");
        output.WriteLine($"  compile+load+run     {Stats(p.CompileRun)}");
        output.WriteLine($"  TOTAL                {Stats(p.Total)}");
    }

    static double P50(List<double> xs) => xs.Order().ElementAt(xs.Count / 2);

    [Fact]
    public void Warm_edit_to_output_is_under_300ms()
    {
        // Run order matters because of tiered JIT, so measure incremental before and after.
        var early = Measure(incremental: true, warmup: 5, measured: 40);
        Report("incremental, 5 warm-up edits", early);
        var fresh = Measure(incremental: false, warmup: 30, measured: 40);
        Report("fresh compilations each edit", fresh);
        var incremental = Measure(incremental: true, warmup: 30, measured: 40);
        Report("incremental, after fresh run", incremental);

        // CI runners are shared and noisy; the acceptance number is for a typical dev laptop.
        if (Environment.GetEnvironmentVariable("CI") is null)
            Assert.True(P50(incremental.Total) < 300, $"warm p50 was {P50(incremental.Total):F1} ms");
    }
}

/// <summary>Builds a notebook from raw text with brand-new compilations (the slow path).</summary>
file sealed class SpikeNotebookFactory(string text)
{
    public SpikeNotebook Build()
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(text, SpikeNotebook.ParseOptions, path: "notebook.cs");
        var usings = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(SpikeNotebook.ImplicitGlobalUsings, SpikeNotebook.ParseOptions, path: "GlobalUsings.g.cs");
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Notebook", [usings, tree], Refs.Framework,
            new(Microsoft.CodeAnalysis.OutputKind.ConsoleApplication, nullableContextOptions: Microsoft.CodeAnalysis.NullableContextOptions.Enable));
        return new SpikeNotebook
        {
            Text = text,
            Tree = tree,
            Compilation = compilation,
            Model = compilation.GetSemanticModel(tree),
            Cells = SpikeNotebook.FindCells(text),
        };
    }
}
