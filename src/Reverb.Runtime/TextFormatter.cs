using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Reverb;

/// <summary>A sequence laid out as rows and columns of display text.</summary>
public sealed record TableData(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, int Total)
{
    public bool Truncated => Rows.Count < Total;
}

/// <summary>
/// Plain-text rendering, used by the console host and as the text/plain part of every output:
/// aligned tables for sequences, indented property lists for objects.
/// </summary>
public static class TextFormatter
{
    public const int MaxRows = 1000;
    public const int MaxTextRows = 100;
    public const int MaxColumnWidth = 40;
    const int MaxDepth = 3;

    public static string Format(object? value) => Format(value, 0, NewSeen());

    static HashSet<object> NewSeen() => new(ReferenceEqualityComparer.Instance);

    static string Format(object? value, int depth, HashSet<object> seen)
    {
        switch (value)
        {
            case null: return "null";
            case string s: return s;
            case Widget w: return w.ToString();
            case IFormattable f when IsScalar(value.GetType()): return f.ToString(null, CultureInfo.CurrentCulture);
        }
        var type = value.GetType();
        if (IsScalar(type)) return value.ToString() ?? "";
        if (!type.IsValueType && !seen.Add(value)) return "(already shown above)";
        try
        {
            if (Table(value, MaxTextRows, depth, seen) is { } table) return RenderText(table);
            return Properties(value, depth, seen);
        }
        finally
        {
            if (!type.IsValueType) seen.Remove(value);
        }
    }

    /// <summary>The value as a table, or null if it isn't a sequence (strings aren't).</summary>
    public static TableData? Table(object? value, int maxRows = MaxRows) =>
        value is null ? null : Table(value, maxRows, 0, NewSeen());

    static TableData? Table(object value, int maxRows, int depth, HashSet<object> seen)
    {
        IEnumerable<object?> rows;
        if (value is IDictionary dict) rows = dict.Cast<DictionaryEntry>().Select(e => (object?)new KeyValue(e.Key, e.Value));
        else if (value is IEnumerable seq and not string) rows = seq.Cast<object?>();
        else return null;

        var kept = new List<object?>();
        var total = 0;
        foreach (var r in rows)
        {
            if (kept.Count < maxRows) kept.Add(r);
            total++;
        }

        var elementType = kept.FirstOrDefault(r => r is not null)?.GetType();
        var members = elementType is null || IsScalar(elementType) || elementType == typeof(string) ? [] : Members(elementType);
        var columns = members.Count == 0 ? ["value"] : members.Select(m => m.Name).ToList();
        var cells = kept.Select(r => (IReadOnlyList<string>)(members.Count == 0
            ? [Cell(r, depth, seen)]
            : members.Select(m => Cell(r is null ? null : Read(m, r), depth, seen)).ToList())).ToList();
        return new TableData(columns, cells, total);
    }

    sealed record KeyValue(object Key, object? Value);

    public static string RenderText(TableData table)
    {
        if (table.Total == 0) return "(empty)";
        var widths = table.Columns.Select((h, i) => Math.Max(h.Length, table.Rows.Max(row => Clip(row[i]).Length))).ToList();
        var sb = new StringBuilder();
        sb.Append(string.Join("  ", table.Columns.Select((h, i) => h.PadRight(widths[i]))).TrimEnd()).Append('\n');
        sb.Append(string.Join("  ", widths.Select(w => new string('-', w)))).Append('\n');
        foreach (var row in table.Rows)
            sb.Append(string.Join("  ", row.Select((c, i) => Clip(c).PadRight(widths[i]))).TrimEnd()).Append('\n');
        if (table.Truncated) sb.Append(CultureInfo.InvariantCulture, $"(showing {table.Rows.Count} of {table.Total} rows)\n");
        return sb.ToString().TrimEnd('\n');
    }

    static string Clip(string text) => text.Length <= MaxColumnWidth ? text : text[..(MaxColumnWidth - 1)] + "…";

    static bool IsScalar(Type t) =>
        t.IsPrimitive || t.IsEnum || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(DateTimeOffset)
        || t == typeof(TimeSpan) || t == typeof(Guid) || t == typeof(DateOnly) || t == typeof(TimeOnly)
        || t == typeof(Uri) || t == typeof(System.Numerics.BigInteger) || t == typeof(Half);

    static string Properties(object value, int depth, HashSet<object> seen)
    {
        var members = Members(value.GetType());
        if (members.Count == 0) return value.ToString() ?? "";
        if (depth >= MaxDepth) return value.ToString() ?? "";
        var sb = new StringBuilder();
        var width = members.Max(m => m.Name.Length);
        foreach (var m in members)
        {
            var text = Format(Read(m, value), depth + 1, seen);
            var lines = text.Split('\n');
            sb.Append(m.Name.PadRight(width)).Append(" : ").Append(lines[0]).Append('\n');
            foreach (var line in lines.Skip(1)) sb.Append(' ', width + 3).Append(line).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Items shown in a table cell that holds a sequence, before "…".</summary>
    const int MaxCellItems = 10;

    static string Cell(object? value, int depth, HashSet<object> seen)
    {
        var text = value switch
        {
            null or string => Format(value, depth + 1, seen),
            _ when IsScalar(value.GetType()) => Format(value, depth + 1, seen),
            // A list in a cell reads "a, b, c" rather than System.String[].
            IEnumerable items and not IDictionary => ItemList(items, depth, seen),
            _ => value.ToString() ?? "",
        };
        return text.Replace('\n', ' ').Replace('\r', ' ');
    }

    static string ItemList(IEnumerable items, int depth, HashSet<object> seen)
    {
        var shown = new List<string>();
        var count = 0;
        foreach (var item in items)
        {
            if (++count <= MaxCellItems)
                shown.Add(item is null || item is string || IsScalar(item.GetType()) ? Format(item, depth + 1, seen) : item.ToString() ?? "");
        }
        if (count == 0) return "(empty)";
        return count > MaxCellItems ? $"{string.Join(", ", shown)}, … ({count} in all)" : string.Join(", ", shown);
    }

    static List<MemberInfo> Members(Type type) =>
        [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead && p.Name != "EqualityContract")
                .Cast<MemberInfo>()
            .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance))];

    static object? Read(MemberInfo m, object target)
    {
        try
        {
            return m switch
            {
                PropertyInfo p => p.GetValue(target),
                FieldInfo f => f.GetValue(target),
                _ => null,
            };
        }
        catch (TargetInvocationException e)
        {
            return $"({e.InnerException?.GetType().Name ?? "error"})";
        }
    }
}
