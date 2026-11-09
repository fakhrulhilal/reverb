using System.Collections.Immutable;
using System.Text;

namespace Reverb.Analysis;

/// <summary><see cref="From"/> depends on <see cref="To"/> through the listed names.</summary>
public sealed record DependencyEdge(string From, string To, ImmutableArray<string> Via);

/// <summary>
/// Which cells depend on which. Nodes are code cells and the types cell, in file order. The setup
/// cell isn't a node: every code cell depends on it implicitly.
/// </summary>
public sealed class DependencyGraph
{
    readonly Dictionary<string, int> order;
    readonly ILookup<string, DependencyEdge> byFrom;
    readonly ILookup<string, DependencyEdge> byTo;

    public DependencyGraph(IEnumerable<string> cellsInFileOrder, IEnumerable<DependencyEdge> edges)
    {
        Cells = [.. cellsInFileOrder];
        Edges = [.. edges.OrderBy(e => IndexOf(e.From)).ThenBy(e => IndexOf(e.To))];
        order = Cells.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i, StringComparer.Ordinal);
        byFrom = Edges.ToLookup(e => e.From, StringComparer.Ordinal);
        byTo = Edges.ToLookup(e => e.To, StringComparer.Ordinal);

        int IndexOf(string key) => Cells.IndexOf(key);
    }

    public ImmutableArray<string> Cells { get; }
    public ImmutableArray<DependencyEdge> Edges { get; }

    public IEnumerable<DependencyEdge> DependenciesOf(string key) => byFrom[key];
    public IEnumerable<DependencyEdge> DependentsOf(string key) => byTo[key];

    /// <summary>Every cell that transitively depends on <paramref name="key"/>, in file order, not including it.</summary>
    public ImmutableArray<string> Downstream(string key)
    {
        if (key == "setup") return [.. Cells.Where(c => c != "types")];
        return Sorted(Reach(byTo[key].Select(e => e.From)));
    }

    /// <summary>Every cell <paramref name="key"/> transitively depends on, in file order, not including it.</summary>
    public ImmutableArray<string> Upstream(string key)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(byFrom[key].Select(e => e.To));
        while (queue.Count > 0)
        {
            var k = queue.Dequeue();
            if (!seen.Add(k)) continue;
            foreach (var e in byFrom[k]) queue.Enqueue(e.To);
        }
        return Sorted(seen);
    }

    /// <summary>
    /// The cells to re-run when the variable <paramref name="name"/> (declared in
    /// <paramref name="declaringCell"/>) changes without its cell re-running, e.g. a widget value:
    /// every cell that reads it, plus their dependents. Not the declaring cell itself.
    /// </summary>
    public ImmutableArray<string> ReadersOf(string declaringCell, string name) =>
        Sorted(Reach(byTo[declaringCell].Where(e => e.Via.Contains(name)).Select(e => e.From)));

    /// <summary>The cell itself followed by everything downstream of it.</summary>
    public ImmutableArray<string> RunSet(string key) => [key, .. Downstream(key)];

    HashSet<string> Reach(IEnumerable<string> start)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(start);
        while (queue.Count > 0)
        {
            var k = queue.Dequeue();
            if (!seen.Add(k)) continue;
            foreach (var e in byTo[k]) queue.Enqueue(e.From);
        }
        return seen;
    }

    ImmutableArray<string> Sorted(IEnumerable<string> keys) =>
        [.. keys.Where(order.ContainsKey).OrderBy(k => order[k])];

    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var e in Edges) sb.Append(e.From).Append(" -> ").Append(e.To).Append(" [").Append(string.Join(", ", e.Via)).Append("]\n");
        return sb.ToString();
    }
}
