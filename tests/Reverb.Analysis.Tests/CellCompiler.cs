using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Reverb.Kernel
{
    // Stand-in with the shape generated code expects. The real one arrives with Reverb.Kernel.
    public sealed class CellContext
    {
        public ValueStore Store { get; } = new();
        public string[] Args { get; init; } = [];
        public CancellationToken CancellationToken { get; init; }
        public List<object> Deferred { get; } = [];
        public void DisposeLater(IDisposable? d) { if (d is not null) Deferred.Add(d); }
        public void DisposeLaterAsync(IAsyncDisposable? d) { if (d is not null) Deferred.Add(d); }
    }

    public sealed class ValueStore
    {
        readonly Dictionary<string, object?> values = [];
        public T Get<T>(string name) => (T)values[name]!;
        public void Set(string name, object? value) => values[name] = value;
    }
}

namespace Reverb.Analysis.Tests
{
    /// <summary>Compiles generated code the way the kernel will, so tests can check it builds.</summary>
    static class CellCompiler
    {
        public static ImmutableArray<Diagnostic> CompileAll(NotebookAnalysis analysis)
        {
            var errors = ImmutableArray.CreateBuilder<Diagnostic>();
            var references = analysis.References.References.Add(MetadataReference.CreateFromFile(typeof(Kernel.CellContext).Assembly.Location));

            var types = CellCodeGenerator.GenerateTypes(analysis);
            if (types.Success)
            {
                var (image, diags) = Emit(analysis, CellCodeGenerator.TypesAssemblyName, types.Code!.Source, references);
                errors.AddRange(diags);
                if (image is not null) references = references.Add(MetadataReference.CreateFromImage(image));
            }

            foreach (var cell in analysis.Cells.Where(c => c.Kind == Format.CellKind.Code))
            {
                var generated = CellCodeGenerator.GenerateCell(analysis, cell.Key);
                if (!generated.Success) continue;
                errors.AddRange(Emit(analysis, CellCodeGenerator.CellsAssemblyName, generated.Code!.Source, references).Errors);
            }
            return errors.ToImmutable();
        }

        public static (byte[]? Image, ImmutableArray<Diagnostic> Errors) Emit(NotebookAnalysis analysis, string assemblyName, string source, IEnumerable<MetadataReference> references)
        {
            var options = analysis.Options;
            var compilation = CSharpCompilation.Create(assemblyName,
                [
                    CSharpSyntaxTree.ParseText(options.GlobalUsingsSource, options.ParseOptions, "GlobalUsings.g.cs", Encoding.UTF8),
                    CSharpSyntaxTree.ParseText(source, options.ParseOptions, assemblyName + ".g.cs", Encoding.UTF8),
                ],
                references,
                options.CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var pe = new MemoryStream();
            var result = compilation.Emit(pe);
            var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
            return (result.Success ? pe.ToArray() : null, errors);
        }
    }
}
