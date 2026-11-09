using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Reverb.Spikes.Tests;

// Spike: per-cell code generation and collectible load contexts.
// Deliberately minimal. Reverb.Kernel will replace this.

public sealed class ValueStore
{
    readonly Dictionary<string, object?> values = new(StringComparer.Ordinal);

    public T Get<T>(string name) => values.TryGetValue(name, out var v)
        ? (T)v!
        : throw new KeyNotFoundException($"No value for '{name}' in the store.");

    public void Set(string name, object? value) => values[name] = value;
    public bool Remove(string name) => values.Remove(name);
    public bool Contains(string name) => values.ContainsKey(name);
    public void Clear() => values.Clear();
}

public sealed class CellContext(ValueStore store)
{
    public ValueStore Store { get; } = store;
    public CancellationToken CancellationToken { get; init; }
}

public sealed record Variable(string Name, string Type);

public sealed record CellSignature(string CellId, string Body, int FirstLine, IReadOnlyList<Variable> Inputs, IReadOnlyList<Variable> Outputs);

public static class CellCodegen
{
    public const string CellsAssemblyName = "Reverb.Generated.Cells";
    public const string TypesAssemblyName = "Reverb.Generated.Types";

    static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>Works out what the generated method needs: typed inputs, typed outputs, the body text.</summary>
    public static CellSignature Signature(SpikeNotebook nb, string cellId)
    {
        var cell = nb.Cells.Single(c => c.Id == cellId);
        var statements = nb.GlobalStatements(cell);
        var topLevel = nb.TopLevelSymbols();
        var flow = nb.Model.AnalyzeDataFlow(statements[0].Statement, statements[^1].Statement)!;
        bool Foreign(ISymbol s) => nb.DeclaringCell(s) != cellId;

        var inputs = new List<Variable>();
        foreach (var s in flow.DataFlowsIn.Union(flow.ReadInside, SymbolEqualityComparer.Default)
                     .Where(s => (topLevel.Contains(s) && Foreign(s)) || s is IParameterSymbol { Name: "args" })
                     .OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            inputs.Add(new Variable(s.Name, TypeOf(s)));
        }
        // Local functions from other cells arrive as delegates.
        var usedFunctions = statements.SelectMany(g => g.DescendantNodes())
            .Select(n => nb.Model.GetSymbolInfo(n).Symbol)
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.LocalFunction && topLevel.Contains(m) && Foreign(m))
            .Distinct(SymbolEqualityComparer.Default)
            .Cast<IMethodSymbol>();
        foreach (var f in usedFunctions) inputs.Add(new Variable(f.Name, DelegateTypeOf(f)));

        // Only store what is definitely assigned when the cell finishes; anything else can't be
        // read by a later cell without a compile error anyway.
        var outputs = flow.VariablesDeclared
            .Where(s => topLevel.Contains(s) && flow.DefinitelyAssignedOnExit.Contains(s, SymbolEqualityComparer.Default))
            .Select(s => new Variable(s.Name, TypeOf(s)))
            .Concat(topLevel.OfType<IMethodSymbol>().Where(m => !Foreign(m)).Select(m => new Variable(m.Name, DelegateTypeOf(m))))
            .ToList();

        var text = nb.Tree.GetText();
        var firstLine = text.Lines.GetLineFromPosition(statements[0].SpanStart);
        var lastLine = text.Lines.GetLineFromPosition(statements[^1].Span.End);
        var markerLine = text.Lines.GetLineFromPosition(cell.Span.Start).LineNumber;
        var body = text.ToString(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(firstLine.Start, lastLine.End));
        return new CellSignature(cellId, body, firstLine.LineNumber - markerLine, inputs, outputs);
    }

    static string TypeOf(ISymbol s) => s switch
    {
        ILocalSymbol l => l.Type.ToDisplayString(TypeFormat),
        IParameterSymbol p => p.Type.ToDisplayString(TypeFormat),
        _ => throw new NotSupportedException(s.Kind.ToString()),
    };

    static string DelegateTypeOf(IMethodSymbol m)
    {
        if (m.IsGenericMethod || m.Parameters.Any(p => p.RefKind != RefKind.None))
            throw new NotSupportedException($"Local function '{m.Name}' can't be shared between cells as a delegate.");
        var ps = m.Parameters.Select(p => p.Type.ToDisplayString(TypeFormat)).ToList();
        if (m.ReturnsVoid)
            return ps.Count == 0 ? "global::System.Action" : $"global::System.Action<{string.Join(", ", ps)}>";
        ps.Add(m.ReturnType.ToDisplayString(TypeFormat));
        return $"global::System.Func<{string.Join(", ", ps)}>";
    }

    public static string Generate(CellSignature sig, string setupUsings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// <auto-generated/> for cell \"{sig.CellId}\"");
        // `var` locals of reference type are declared nullable (T?), so inputs come out as T?.
        // Diagnostics come from the analysis compilation, so nullable warnings here are noise.
        sb.AppendLine("#nullable enable annotations");
        sb.AppendLine("#pragma warning disable CS1998 // async method without await");
        sb.AppendLine(setupUsings);
        sb.AppendLine("namespace Reverb.Generated.Cells;");
        sb.AppendLine($"public static class Cell_{Sanitize(sig.CellId)}");
        sb.AppendLine("{");
        sb.AppendLine($"    public static async global::System.Threading.Tasks.Task RunAsync(global::{typeof(CellContext).FullName} __ctx)");
        sb.AppendLine("    {");
        foreach (var v in sig.Inputs)
            sb.AppendLine($"        var {v.Name} = __ctx.Store.Get<{v.Type}>(\"{v.Name}\");");
        sb.AppendLine($"#line {sig.FirstLine} \"cell:{sig.CellId}\"");
        sb.AppendLine(sig.Body);
        sb.AppendLine("#line default");
        foreach (var v in sig.Outputs)
            sb.AppendLine($"        __ctx.Store.Set(\"{v.Name}\", ({v.Type}){v.Name});");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    static string Sanitize(string id) => id.Replace('-', '_');
}

/// <summary>A compiled cell assembly living in its own collectible load context.</summary>
public sealed class CompiledCell
{
    public required string Key { get; init; }
    public required CellLoadContext Context { get; init; }
    public required Func<CellContext, Task> Run { get; init; }
}

public sealed class CellLoadContext(string name, Func<AssemblyName, Assembly?> resolve)
    : AssemblyLoadContext(name, isCollectible: true)
{
    protected override Assembly? Load(AssemblyName assemblyName) => resolve(assemblyName);
}

public sealed class SpikeKernel
{
    public ValueStore Store { get; } = new();
    public int CompileCount { get; private set; }
    public int RunCount { get; private set; }
    public List<WeakReference> Unloaded { get; } = [];

    readonly Dictionary<string, CompiledCell> cells = new(StringComparer.Ordinal);
    readonly ImmutableArray<MetadataReference> baseReferences =
        Refs.Framework.Add(MetadataReference.CreateFromFile(typeof(CellContext).Assembly.Location));

    CellLoadContext? typesContext;
    Assembly? typesAssembly;
    MetadataReference? typesReference;
    string typesVersion = "none";

    public string SetupUsings { get; set; } = "";

    /// <summary>Compile the types cell into its own collectible context. Old cells must be recompiled after this.</summary>
    public void SetTypes(string? typesSource)
    {
        if (typesContext is not null)
        {
            Unloaded.Add(new WeakReference(typesContext));
            typesContext.Unload();
            typesContext = null;
            typesAssembly = null;
            typesReference = null;
        }
        typesVersion = Hash(typesSource ?? "");
        if (string.IsNullOrWhiteSpace(typesSource)) return;

        var source = $"[assembly: global::System.Runtime.CompilerServices.InternalsVisibleTo(\"{CellCodegen.CellsAssemblyName}\")]\n"
            + SetupUsings + "\n#line 1 \"cell:types\"\n" + typesSource;
        var (pe, pdb) = Emit(CellCodegen.TypesAssemblyName, source, baseReferences);
        typesReference = MetadataReference.CreateFromImage(pe);
        typesContext = new CellLoadContext("types", _ => null);
        typesAssembly = typesContext.LoadFromStream(new MemoryStream(pe), new MemoryStream(pdb));
    }

    /// <summary>Compile (or reuse) and run one cell.</summary>
    public async Task RunCellAsync(CellSignature sig, CancellationToken ct = default)
    {
        var compiled = GetOrCompile(sig);
        RunCount++;
        await compiled.Run(new CellContext(Store) { CancellationToken = ct });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    CompiledCell GetOrCompile(CellSignature sig)
    {
        var key = Hash(string.Join("\n", sig.Body, sig.FirstLine,
            string.Join(",", sig.Inputs), string.Join(",", sig.Outputs), SetupUsings, typesVersion));
        if (cells.TryGetValue(sig.CellId, out var existing))
        {
            if (existing.Key == key) return existing;
            Evict(sig.CellId);
        }

        var source = CellCodegen.Generate(sig, SetupUsings);
        var references = typesReference is null ? baseReferences : baseReferences.Add(typesReference);
        var (pe, pdb) = Emit(CellCodegen.CellsAssemblyName, source, references);
        CompileCount++;

        var types = typesAssembly;
        var alc = new CellLoadContext($"cell:{sig.CellId}",
            n => n.Name == CellCodegen.TypesAssemblyName ? types : null);
        var asm = alc.LoadFromStream(new MemoryStream(pe), new MemoryStream(pdb));
        var method = asm.GetType($"Reverb.Generated.Cells.Cell_{sig.CellId.Replace('-', '_')}", throwOnError: true)!
            .GetMethod("RunAsync")!;
        var run = method.CreateDelegate<Func<CellContext, Task>>();
        var compiled = new CompiledCell { Key = key, Context = alc, Run = run };
        cells[sig.CellId] = compiled;
        return compiled;
    }

    public void Evict(string cellId)
    {
        if (!cells.Remove(cellId, out var old)) return;
        Unloaded.Add(new WeakReference(old.Context));
        old.Context.Unload();
    }

    public void EvictAll()
    {
        foreach (var id in cells.Keys.ToList()) Evict(id);
    }

    /// <summary>
    /// When true, cell compilations are derived from a cached base compilation (global usings +
    /// references) with AddSyntaxTrees, so Roslyn reuses its reference manager and bound global
    /// usings. When false, every emit starts from CSharpCompilation.Create. Used by the latency spike.
    /// </summary>
    public static bool ReuseBaseCompilation { get; set; } = true;

    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, MetadataReference), CSharpCompilation> baseCompilations = new();

    static CSharpCompilation BaseCompilation(string assemblyName, IReadOnlyList<MetadataReference> references)
    {
        CSharpCompilation Create() => CSharpCompilation.Create(assemblyName,
            [CSharpSyntaxTree.ParseText(SpikeNotebook.ImplicitGlobalUsings, SpikeNotebook.ParseOptions, path: "GlobalUsings.g.cs", encoding: Encoding.UTF8)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                optimizationLevel: OptimizationLevel.Debug));
        return ReuseBaseCompilation
            ? baseCompilations.GetOrAdd((assemblyName, references[^1]), _ => Create())
            : Create();
    }

    public static (byte[] Pe, byte[] Pdb) Emit(string assemblyName, string source, IEnumerable<MetadataReference> references)
    {
        var compilation = BaseCompilation(assemblyName, references.ToList())
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(source, SpikeNotebook.ParseOptions, path: $"{assemblyName}.g.cs", encoding: Encoding.UTF8));
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var result = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        if (!result.Success)
        {
            var errors = string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            throw new CellCompileException(errors, result.Diagnostics);
        }
        return (pe.ToArray(), pdb.ToArray());
    }

    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];
}

public sealed class CellCompileException(string message, ImmutableArray<Diagnostic> diagnostics) : Exception(message)
{
    public ImmutableArray<Diagnostic> Diagnostics { get; } = diagnostics;
}

public static class Unload
{
    /// <summary>Run the GC until every weak reference is dead, or give up.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool AllCollected(IEnumerable<WeakReference> refs, int attempts = 20)
    {
        var list = refs.ToList();
        for (var i = 0; i < attempts && list.Any(r => r.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        return list.All(r => !r.IsAlive);
    }
}
