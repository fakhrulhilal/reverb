namespace Reverb.Kernel;

/// <summary>
/// Variable values shared between cells. Each value remembers which cell set it, so clearing a
/// cell never removes a variable that another cell has since declared.
/// </summary>
public sealed class ValueStore
{
    readonly Dictionary<string, (object? Value, string Owner)> values = new(StringComparer.Ordinal);

    /// <summary>The cell whose values are being set. The session sets this before each cell runs.</summary>
    internal string CurrentOwner { get; set; } = "";

    public T Get<T>(string name)
    {
        if (!values.TryGetValue(name, out var entry))
            throw new InvalidOperationException($"'{name}' has no value yet. Run the cell that declares it first.");
        if (entry.Value is T typed) return typed;
        if (entry.Value is null && default(T) is null) return default!;
        throw new InvalidCastException(
            $"'{name}' holds a {entry.Value?.GetType().FullName ?? "null"}, but this cell expects {typeof(T).FullName}. Re-run the cell that declares it.");
    }

    public void Set(string name, object? value) => values[name] = (value, CurrentOwner);

    public bool Contains(string name) => values.ContainsKey(name);

    public bool TryGet(string name, out object? value)
    {
        var found = values.TryGetValue(name, out var entry);
        value = entry.Value;
        return found;
    }

    public string? OwnerOf(string name) => values.TryGetValue(name, out var e) ? e.Owner : null;

    /// <summary>Removes every value the given cell set. Returns the names removed.</summary>
    public IReadOnlyList<string> RemoveOwnedBy(string cell)
    {
        var names = values.Where(kv => kv.Value.Owner == cell).Select(kv => kv.Key).ToList();
        foreach (var n in names) values.Remove(n);
        return names;
    }

    public IReadOnlyCollection<string> Names => values.Keys;

    public IReadOnlyDictionary<string, object?> Snapshot() => values.ToDictionary(kv => kv.Key, kv => kv.Value.Value, StringComparer.Ordinal);

    public void Clear() => values.Clear();
}

/// <summary>What a running cell gets from the kernel. Generated code is the only user.</summary>
public sealed class CellContext
{
    internal CellContext(ValueStore store, string[] args, CancellationToken token)
    {
        Store = store;
        Args = args;
        CancellationToken = token;
    }

    public ValueStore Store { get; }
    public string[] Args { get; }
    public CancellationToken CancellationToken { get; }

    internal List<object> Deferred { get; } = [];

    /// <summary>Keeps a top-level <c>using var</c> value alive until the cell's outputs are replaced.</summary>
    public void DisposeLater(IDisposable? value)
    {
        if (value is not null) Deferred.Add(value);
    }

    public void DisposeLaterAsync(IAsyncDisposable? value)
    {
        if (value is not null) Deferred.Add(value);
    }
}
