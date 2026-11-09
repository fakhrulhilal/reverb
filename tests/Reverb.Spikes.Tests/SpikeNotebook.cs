using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Reverb.Spikes.Tests;

/// <summary>
/// Throwaway version of the analysis compilation (the whole notebook compiled as one program).
/// Cells get ids c1..cN, plus "setup" and "types" when given.
/// </summary>
public sealed class SpikeNotebook
{
    // Same list the SDK generates for ImplicitUsings=enable in a Microsoft.NET.Sdk console app.
    public const string ImplicitGlobalUsings = """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        """;

    public static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    public required string Text { get; init; }
    public required SyntaxTree Tree { get; init; }
    public required CSharpCompilation Compilation { get; init; }
    public required SemanticModel Model { get; init; }
    public required IReadOnlyList<SpikeCell> Cells { get; init; }

    public static SpikeNotebook Create(string[] cells, string? types = null, string? setup = null,
        IEnumerable<MetadataReference>? extraReferences = null)
    {
        var sb = new System.Text.StringBuilder();
        var starts = new List<(string Id, int Start)>();
        if (setup is not null)
        {
            starts.Add(("setup", sb.Length));
            sb.Append("// %% setup\n").Append(setup).Append('\n');
        }
        for (var i = 0; i < cells.Length; i++)
        {
            starts.Add(($"c{i + 1}", sb.Length));
            sb.Append($"// %% id=c{i + 1}\n").Append(cells[i]).Append('\n');
        }
        if (types is not null)
        {
            starts.Add(("types", sb.Length));
            sb.Append("// %% types\n").Append(types).Append('\n');
        }

        var text = sb.ToString();
        var tree = CSharpSyntaxTree.ParseText(text, ParseOptions, path: "notebook.cs");
        var usingsTree = CSharpSyntaxTree.ParseText(ImplicitGlobalUsings, ParseOptions, path: "GlobalUsings.g.cs");
        var compilation = CSharpCompilation.Create(
            "Notebook",
            [usingsTree, tree],
            Refs.Framework.AddRange(extraReferences ?? []),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable));

        var spikeCells = new List<SpikeCell>();
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1].Start : text.Length;
            spikeCells.Add(new SpikeCell(starts[i].Id, TextSpan.FromBounds(starts[i].Start, end)));
        }

        return new SpikeNotebook
        {
            Text = text,
            Tree = tree,
            Compilation = compilation,
            Model = compilation.GetSemanticModel(tree),
            Cells = spikeCells,
        };
    }

    /// <summary>
    /// The editor path: incremental reparse plus ReplaceSyntaxTree, so the new compilation shares
    /// references and the global-usings tree with the old one.
    /// </summary>
    public SpikeNotebook WithText(string newText)
    {
        var tree = Tree.WithChangedText(Microsoft.CodeAnalysis.Text.SourceText.From(newText));
        var compilation = Compilation.ReplaceSyntaxTree(Tree, tree);
        return new SpikeNotebook
        {
            Text = newText,
            Tree = tree,
            Compilation = compilation,
            Model = compilation.GetSemanticModel(tree),
            Cells = FindCells(newText),
        };
    }

    /// <summary>Cells from `// %%` marker lines (good enough for the spike; Reverb.Format does this properly).</summary>
    public static IReadOnlyList<SpikeCell> FindCells(string text)
    {
        var starts = new List<(string Id, int Start)>();
        var pos = 0;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("// %% id=", StringComparison.Ordinal)) starts.Add((line["// %% id=".Length..].Trim(), pos));
            else if (line.StartsWith("// %% types", StringComparison.Ordinal)) starts.Add(("types", pos));
            else if (line.StartsWith("// %% setup", StringComparison.Ordinal)) starts.Add(("setup", pos));
            pos += line.Length + 1;
        }
        return starts.Select((s, i) => new SpikeCell(s.Id,
            TextSpan.FromBounds(s.Start, i + 1 < starts.Count ? starts[i + 1].Start : text.Length))).ToList();
    }

    public CompilationUnitSyntax Root => (CompilationUnitSyntax)Tree.GetRoot();

    public SpikeCell CellAt(int position) => Cells.Single(c => c.Span.Contains(position) || c.Span.End == position);

    public IReadOnlyList<GlobalStatementSyntax> GlobalStatements(SpikeCell cell) =>
        Root.Members.OfType<GlobalStatementSyntax>().Where(g => cell.Span.Contains(g.SpanStart)).ToList();

    public ImmutableArray<Diagnostic> Errors =>
        Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();

    /// <summary>
    /// Locals and local functions whose scope is the top level of the program. Taken from the
    /// IOperation tree: the outermost block of the entry point lists exactly the locals scoped
    /// to it. (A LookupSymbols probe at the first statement is wrong when that statement opens
    /// its own scope, e.g. a block or a using statement.)
    /// </summary>
    public ImmutableHashSet<ISymbol> TopLevelSymbols()
    {
        var body = TopLevelBlock();
        if (body is null) return ImmutableHashSet<ISymbol>.Empty;
        var localFunctions = body.Operations
            .OfType<Microsoft.CodeAnalysis.Operations.ILocalFunctionOperation>()
            .Select(f => (ISymbol)f.Symbol);
        return body.Locals.Cast<ISymbol>().Concat(localFunctions)
            .ToImmutableHashSet(SymbolEqualityComparer.Default);
    }

    public Microsoft.CodeAnalysis.Operations.IBlockOperation? TopLevelBlock()
    {
        var first = Root.Members.OfType<GlobalStatementSyntax>().FirstOrDefault();
        if (first is null) return null;
        var op = Model.GetOperation(first.Statement);
        while (op?.Parent is not null) op = op.Parent;
        return op switch
        {
            Microsoft.CodeAnalysis.Operations.IMethodBodyOperation m => m.BlockBody,
            Microsoft.CodeAnalysis.Operations.IBlockOperation b => b,
            _ => throw new InvalidOperationException($"Unexpected root operation {op?.Kind}"),
        };
    }

    static bool IsEntryPointArgs(ISymbol s) =>
        s is IParameterSymbol { Name: "args", ContainingSymbol: IMethodSymbol { Name: WellKnownMemberNames.TopLevelStatementsEntryPointMethodName } };

    public string DeclaringCell(ISymbol symbol) =>
        CellAt(symbol.DeclaringSyntaxReferences[0].Span.Start).Id;

    public CellFlow Analyze(string cellId)
    {
        var cell = Cells.Single(c => c.Id == cellId);
        var statements = GlobalStatements(cell);
        var topLevel = TopLevelSymbols();
        if (statements.Count == 0) return new CellFlow(cellId, [], [], [], [], []);

        var flow = Model.AnalyzeDataFlow(statements[0].Statement, statements[^1].Statement)
            ?? throw new InvalidOperationException("AnalyzeDataFlow returned null");
        if (!flow.Succeeded) throw new InvalidOperationException($"AnalyzeDataFlow failed for {cellId}");

        bool Foreign(ISymbol s) => DeclaringCell(s) != cellId;

        var outputs = flow.VariablesDeclared.Where(topLevel.Contains)
            .Concat(topLevel.Where(s => s is IMethodSymbol && !Foreign(s)))
            .Select(s => s.Name);

        var reads = flow.DataFlowsIn.Union(flow.ReadInside, SymbolEqualityComparer.Default);
        var inputs = reads.Where(s => (topLevel.Contains(s) && Foreign(s)) || IsEntryPointArgs(s))
            .Select(s => s.Name);

        var foreignWrites = flow.WrittenInside.Where(s => topLevel.Contains(s) && Foreign(s)).Select(s => s.Name);

        // Cell-level dependencies: variables read, local functions referenced, types-cell types used.
        var deps = new HashSet<string>();
        foreach (var s in reads.Where(s => topLevel.Contains(s) && Foreign(s))) deps.Add(DeclaringCell(s));
        var localFunctionsUsed = new HashSet<string>();
        foreach (var node in statements.SelectMany(g => g.DescendantNodes()))
        {
            var symbol = node switch
            {
                IdentifierNameSyntax or GenericNameSyntax => Model.GetSymbolInfo(node).Symbol,
                _ => null,
            };
            if (symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction } lf && Foreign(lf.OriginalDefinition))
            {
                deps.Add(DeclaringCell(lf.OriginalDefinition));
                localFunctionsUsed.Add(lf.Name);
            }
            var type = symbol as INamedTypeSymbol ?? (node is ExpressionSyntax e ? Model.GetTypeInfo(e).Type as INamedTypeSymbol : null);
            if (type?.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault() is { } r && r.SyntaxTree == Tree
                && CellAt(r.Span.Start).Id == "types")
            {
                deps.Add("types");
            }
        }

        return new CellFlow(
            cellId,
            Sorted(inputs),
            Sorted(outputs),
            Sorted(foreignWrites),
            Sorted(deps),
            Sorted(localFunctionsUsed));
    }

    static string[] Sorted(IEnumerable<string> names) => names.Distinct().Order(StringComparer.Ordinal).ToArray();
}

public sealed record SpikeCell(string Id, TextSpan Span);

public sealed record CellFlow(
    string Id,
    string[] Inputs,
    string[] Outputs,
    string[] ForeignWrites,
    string[] DependsOn,
    string[] LocalFunctionsUsed)
{
    public override string ToString() =>
        $"{Id}: in=[{string.Join(",", Inputs)}] out=[{string.Join(",", Outputs)}] writes=[{string.Join(",", ForeignWrites)}] deps=[{string.Join(",", DependsOn)}] fns=[{string.Join(",", LocalFunctionsUsed)}]";
}

public static class Refs
{
    /// <summary>Framework reference assemblies taken from the running process (managed files only).</summary>
    public static readonly ImmutableArray<MetadataReference> Framework = Load();

    static ImmutableArray<MetadataReference> Load()
    {
        var frameworkDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        return tpa
            .Where(p => string.Equals(Path.GetDirectoryName(p), frameworkDir, StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToImmutableArray();
    }
}
