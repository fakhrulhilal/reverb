using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Reverb.Format;

namespace Reverb.Analysis;

/// <summary>A fix that turned out not to be possible. The message says why, for the user.</summary>
public sealed class CodeFixException(string message) : Exception(message);

/// <summary>
/// Fixes for Reverb's own diagnostics: RVB004 (a using, type or statement in the wrong cell) moves
/// the code where it belongs, and RVB002 (an anonymous type passed between cells) replaces the
/// anonymous type with a record in the types cell.
/// </summary>
public static class NotebookCodeFixes
{
    const string ToSetup = "Move to the setup cell";
    const string ToTypes = "Move to the types cell";
    const string ToNewCell = "Move to a new code cell";
    const string ToRecord = "Use a record instead of the anonymous type";

    static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.MinimallyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>What the fix does, or null if Reverb has no fix for this diagnostic.</summary>
    public static string? Title(NotebookAnalysis analysis, NotebookDiagnostic diagnostic)
    {
        try
        {
            return diagnostic.Id switch
            {
                RuleIds.MisplacedDeclaration => Misplaced(analysis, diagnostic) switch
                {
                    UsingDirectiveSyntax or ExternAliasDirectiveSyntax or TextSpan => ToSetup,
                    GlobalStatementSyntax => ToNewCell,
                    MemberDeclarationSyntax => ToTypes,
                    _ => null,
                },
                RuleIds.AnonymousTypeShared => AnonymousRecord(analysis, diagnostic) is null ? null : ToRecord,
                _ => null,
            };
        }
        catch (CodeFixException)
        {
            return null;
        }
    }

    /// <summary>The notebook with the fix applied.</summary>
    public static NotebookDocument Apply(NotebookAnalysis analysis, NotebookDiagnostic diagnostic) => diagnostic.Id switch
    {
        RuleIds.MisplacedDeclaration => Misplaced(analysis, diagnostic) switch
        {
            UsingDirectiveSyntax u => MoveToSetup(analysis, u.Span),
            ExternAliasDirectiveSyntax e => MoveToSetup(analysis, e.Span),
            TextSpan usingText => MoveToSetup(analysis, usingText),
            GlobalStatementSyntax s => MoveToNewCell(analysis, s),
            MemberDeclarationSyntax m => MoveToTypes(analysis, m),
            _ => throw new CodeFixException("Reverb couldn't find the code this diagnostic is about. Move it by hand."),
        },
        RuleIds.AnonymousTypeShared => ReplaceAnonymousType(analysis, diagnostic),
        _ => throw new CodeFixException($"Reverb has no fix for {diagnostic.Id}."),
    };

    // ---- finding the code --------------------------------------------------------------------------

    static TextSpan FileSpan(NotebookAnalysis a, NotebookDiagnostic d)
    {
        var layout = d.CellKey is null ? null : a.Layout.Find(d.CellKey);
        if (layout is null) throw new CodeFixException("This diagnostic isn't in a cell.");
        var start = a.Map.ToFileOffset(layout, d.Range.StartLine, d.Range.StartColumn);
        var end = a.Map.ToFileOffset(layout, d.Range.EndLine, d.Range.EndColumn);
        return TextSpan.FromBounds(start, Math.Max(start, end));
    }

    static readonly System.Text.RegularExpressions.Regex UsingLine = new(
        @"^(global\s+)?using\s+(static\s+)?[\w.]+(\s*=\s*[\w.<>,\s]+)?\s*;$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The using, member or statement an RVB004 diagnostic points at. A using directive after
    /// statements isn't parsed as one (the compiler reports it), so that comes back as its text span.
    /// </summary>
    static object? Misplaced(NotebookAnalysis a, NotebookDiagnostic d)
    {
        var span = FileSpan(a, d);
        var root = a.Tree.GetCompilationUnitRoot();
        var node = root.Usings.Cast<SyntaxNode>().Concat(root.Externs).Concat(root.Members).FirstOrDefault(n => n.Span.Contains(span.Start));
        if (node is UsingDirectiveSyntax or ExternAliasDirectiveSyntax) return node;
        if (UsingLine.IsMatch(a.Text.ToString(span).Trim())) return span;
        return node;
    }

    // ---- RVB004 --------------------------------------------------------------------------------------

    static NotebookDocument MoveToSetup(NotebookAnalysis a, TextSpan directive)
    {
        var text = a.Text.ToString(directive).Trim();
        var doc = Edit(a, [new TextChange(directive, "")]);
        var setup = doc.SetupCell ?? throw new CodeFixException("This notebook has no setup cell to move the using into.");
        var lines = setup.Source.Split('\n');
        var source = lines.Any(l => l.Trim() == text) ? setup.Source : Append(setup.Source, text);
        return doc.ReplaceCell(doc.IndexOf(setup.Key!), setup with { Source = source });
    }

    static NotebookDocument MoveToTypes(NotebookAnalysis a, MemberDeclarationSyntax member)
    {
        var text = Extract(a, member);
        var doc = Remove(a, member);
        if (doc.TypesCell is { } types)
            return doc.ReplaceCell(doc.IndexOf(types.Key!), types with { Source = Append(types.Source, text) });
        return doc.InsertCell(doc.Cells.Length, NotebookCell.Types(text));
    }

    static NotebookDocument MoveToNewCell(NotebookAnalysis a, GlobalStatementSyntax statement)
    {
        var text = Extract(a, statement);
        var from = a.Map.CellAt(statement.SpanStart)!;
        var doc = Remove(a, statement);
        var taken = doc.Cells.Select(c => c.Id).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var cell = NotebookCell.Code(CellIds.New(Random.Shared, taken), text);
        // Out of setup: first among the code cells. Out of types: last.
        var index = from.Kind == CellKind.Setup ? doc.IndexOf(from.Key!) + 1 : doc.IndexOf(from.Key!);
        return doc.InsertCell(index, cell);
    }

    /// <summary>The node's text with its comments and attributes, without surrounding blank lines.</summary>
    static string Extract(NotebookAnalysis a, SyntaxNode node) =>
        NotebookDocument.NormalizeSource(a.Text.ToString(TextSpan.FromBounds(LeadingStart(a, node), node.Span.End)));

    /// <summary>
    /// Where the node's own leading comments start. Blank lines before them stay behind, and so does
    /// the cell's marker line, which Roslyn sees as a comment on the cell's first statement.
    /// </summary>
    static int LeadingStart(NotebookAnalysis a, SyntaxNode node)
    {
        var cell = a.Map.CellAt(node.SpanStart)!;
        var contentStart = a.Map.ToFileOffset(cell, 0, 0);
        var first = node.GetLeadingTrivia()
            .Where(t => t.FullSpan.Start >= contentStart)
            .FirstOrDefault(t => !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia));
        return first == default ? node.SpanStart : first.FullSpan.Start;
    }

    static NotebookDocument Remove(NotebookAnalysis a, SyntaxNode node) =>
        Edit(a, [new TextChange(TextSpan.FromBounds(LeadingStart(a, node), node.Span.End), "")]);

    static string Append(string source, string text) => source.Trim().Length == 0 ? text : source.TrimEnd() + "\n" + text;

    /// <summary>Applies changes to the notebook file text, cell by cell. Each change must stay inside one cell.</summary>
    static NotebookDocument Edit(NotebookAnalysis a, IEnumerable<TextChange> changes)
    {
        var doc = a.Document;
        foreach (var group in changes.GroupBy(c => a.Map.CellAt(c.Span.Start)))
        {
            var layout = group.Key ?? throw new CodeFixException("The fix would change the notebook's header, so Reverb won't make it.");
            var cell = doc.Cells[layout.CellIndex];
            var source = cell.Source;
            foreach (var change in group.OrderByDescending(c => c.Span.Start))
            {
                if (a.Map.CellAt(change.Span.End) != layout && change.Span.End != a.Map.RegionOf(layout).End)
                    throw new CodeFixException("The fix would change more than one cell, so Reverb won't make it.");
                var from = a.Map.ToSourceOffset(layout, cell.Source, change.Span.Start);
                var to = a.Map.ToSourceOffset(layout, cell.Source, change.Span.End);
                source = source[..from] + change.NewText + source[to..];
            }
            doc = doc.ReplaceCell(layout.CellIndex, cell with { Source = TidyBlankLines(NotebookDocument.NormalizeSource(source)) });
        }
        return doc;
    }

    /// <summary>Removing code can leave a run of blank lines; keep at most one.</summary>
    static string TidyBlankLines(string source)
    {
        var lines = new List<string>();
        foreach (var line in source.Split('\n'))
        {
            if (line.Trim().Length == 0 && lines.Count > 0 && lines[^1].Trim().Length == 0) continue;
            lines.Add(line.Trim().Length == 0 ? "" : line);
        }
        return string.Join('\n', lines);
    }

    // ---- RVB002 --------------------------------------------------------------------------------------

    sealed record RecordPlan(INamedTypeSymbol Anonymous, string Name, string Declaration, List<AnonymousObjectCreationExpressionSyntax> Creations);

    /// <summary>How the anonymous type behind an RVB002 diagnostic would become a record, or null if it can't.</summary>
    static RecordPlan? AnonymousRecord(NotebookAnalysis a, NotebookDiagnostic d)
    {
        var span = FileSpan(a, d);
        var root = a.Tree.GetCompilationUnitRoot();
        var token = root.FindToken(span.Start);
        var local = token.Parent switch
        {
            IdentifierNameSyntax n => a.Model.GetSymbolInfo(n).Symbol as ILocalSymbol,
            VariableDeclaratorSyntax v => a.Model.GetDeclaredSymbol(v) as ILocalSymbol,
            SingleVariableDesignationSyntax s => a.Model.GetDeclaredSymbol(s) as ILocalSymbol,
            _ => null,
        };
        if (local is null) return null;

        var anonymous = AnonymousTypes(local.Type).FirstOrDefault();
        if (anonymous is null) return null;
        var properties = anonymous.GetMembers().OfType<IPropertySymbol>().ToList();
        // A member that's itself anonymous (or otherwise unnameable) can't be written in a record.
        if (properties.Any(p => AnonymousTypes(p.Type).Any() || p.Type.TypeKind == TypeKind.Error)) return null;

        var creations = root.DescendantNodes().OfType<AnonymousObjectCreationExpressionSyntax>()
            .Where(c => SymbolEqualityComparer.Default.Equals(a.Model.GetTypeInfo(c).Type, anonymous))
            .ToList();
        if (creations.Count == 0) return null;

        var name = UnusedName(a, Pascal(local.Name) + "Row");
        var position = creations[0].SpanStart;
        var parameters = properties.Select(p => $"{p.Type.ToMinimalDisplayString(a.Model, position, TypeFormat)} {p.Name}");
        return new RecordPlan(anonymous, name, $"record {name}({string.Join(", ", parameters)});", creations);
    }

    static NotebookDocument ReplaceAnonymousType(NotebookAnalysis a, NotebookDiagnostic d)
    {
        var plan = AnonymousRecord(a, d) ?? throw new CodeFixException(
            "Reverb can't turn this anonymous type into a record (one of its members has an anonymous type too, or it isn't created in this notebook). Write a record in the types cell and use it instead.");

        // Arguments in the order the record declares its members, which is the anonymous type's order.
        var changes = plan.Creations.Select(c =>
            new TextChange(c.Span, $"new {plan.Name}({string.Join(", ", c.Initializers.Select(i => i.Expression.ToString()))})"));
        var doc = Edit(a, changes);
        if (doc.TypesCell is { } types)
            return doc.ReplaceCell(doc.IndexOf(types.Key!), types with { Source = Append(types.Source, plan.Declaration) });
        return doc.InsertCell(doc.Cells.Length, NotebookCell.Types(plan.Declaration));
    }

    static IEnumerable<INamedTypeSymbol> AnonymousTypes(ITypeSymbol type) => type switch
    {
        INamedTypeSymbol { IsAnonymousType: true } named => [named],
        IArrayTypeSymbol array => AnonymousTypes(array.ElementType),
        INamedTypeSymbol named => named.TypeArguments.SelectMany(AnonymousTypes),
        _ => [],
    };

    static string Pascal(string name) => name.Length == 0 ? "Item" : char.ToUpperInvariant(name[0]) + name[1..];

    static string UnusedName(NotebookAnalysis a, string wanted)
    {
        var name = wanted;
        for (var i = 2; a.Compilation.GetSymbolsWithName(name, SymbolFilter.Type).Any() || a.Compilation.GetTypeByMetadataName(name) is not null; i++)
            name = wanted + i;
        return name;
    }
}
