using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Reverb.Format;

namespace Reverb.Analysis;

public enum VariableKind
{
    /// <summary>A top-level local variable.</summary>
    Local,

    /// <summary>A top-level local function, passed between cells as a delegate.</summary>
    LocalFunction,

    /// <summary>The program's <c>args</c>.</summary>
    Args,
}

/// <summary>A variable a cell reads from another cell, or one it declares.</summary>
public sealed record CellVariable(string Name, string Type, VariableKind Kind, string? DeclaringCell)
{
    /// <summary>The type as generated code writes it: fully qualified, with nullable annotations.</summary>
    public required string GeneratedType { get; init; }

    internal ISymbol? Symbol { get; init; }

    public override string ToString() => $"{Name}: {Type}";
}

/// <summary>
/// A top-level <c>using var</c> declaration. Generated code doesn't dispose it when the cell
/// returns; the kernel disposes it when the cell's outputs are replaced, matching the
/// program-lifetime behaviour of <c>dotnet run</c>.
/// </summary>
public sealed record UsingDeclaration(
    TextSpan UsingKeyword,
    TextSpan? AwaitKeyword,
    int StatementEnd,
    ImmutableArray<string> Variables);

/// <summary>What analysis found for one cell.</summary>
public sealed record CellAnalysis
{
    public required string Key { get; init; }
    public required int Index { get; init; }
    public required CellKind Kind { get; init; }

    /// <summary>Variables, local functions and <c>args</c> this cell reads from elsewhere.</summary>
    public ImmutableArray<CellVariable> Inputs { get; init; } = [];

    /// <summary>Every top-level variable and local function this cell declares.</summary>
    public ImmutableArray<CellVariable> Outputs { get; init; } = [];

    /// <summary>
    /// The outputs generated code saves to the value store: definitely assigned when the cell
    /// finishes, and of a type that can cross cells.
    /// </summary>
    public ImmutableArray<CellVariable> Stored { get; init; } = [];

    /// <summary>True when the cell uses a type declared in the types cell.</summary>
    public bool UsesTypes { get; init; }

    public ImmutableArray<UsingDeclaration> UsingDeclarations { get; init; } = [];

    public ImmutableArray<NotebookDiagnostic> Diagnostics { get; init; } = [];

    public bool HasErrors => Diagnostics.Any(d => d.IsError);
}
