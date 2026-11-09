using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Reverb;

/// <summary>
/// Plain-text rendering used by the console host: aligned tables for sequences, indented
/// property lists for objects.
/// </summary>
public static class TextFormatter
{
    public const int MaxRows = 100;
    public const int MaxColumnWidth = 40;
    const int MaxDepth = 3;

    public static string Format(object? value) => Format(value, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));

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
            if (value is IDictionary dict) return Table(dict.Cast<DictionaryEntry>().Select(e => (object)new { Key = e.Key, Value = e.Value }), depth, seen);
            if (value is IEnumerable seq) return Table(seq.Cast<object?>(), depth, seen);
            return Properties(value, depth, seen);
        }
        finally
        {
            if (!type.IsValueType) seen.Remove(value);
        }
    }

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

    static string Table(IEnumerable<object?> rows, int depth, HashSet<object> seen)
    {
        var all = new List<object?>();
        var total = 0;
        foreach (var r in rows)
        {
            if (all.Count < MaxRows) all.Add(r);
            total++;
        }
        if (total == 0) return "(empty)";

        var elementType = all.FirstOrDefault(r => r is not null)?.GetType();
        var members = elementType is null || IsScalar(elementType) || elementType == typeof(string) ? [] : Members(elementType);
        var headers = members.Count == 0 ? ["value"] : members.Select(m => m.Name).ToList();
        var cells = all.Select(r => members.Count == 0
            ? [Cell(r, depth, seen)]
            : members.Select(m => Cell(r is null ? null : Read(m, r), depth, seen)).ToList()).ToList();

        var widths = headers.Select((h, i) => Math.Max(h.Length, cells.Max(row => row[i].Length))).ToList();
        var sb = new StringBuilder();
        sb.Append(string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))).TrimEnd()).Append('\n');
        sb.Append(string.Join("  ", widths.Select(w => new string('-', w)))).Append('\n');
        foreach (var row in cells)
            sb.Append(string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))).TrimEnd()).Append('\n');
        if (total > all.Count) sb.Append(CultureInfo.InvariantCulture, $"(showing {all.Count} of {total} rows)\n");
        return sb.ToString().TrimEnd('\n');
    }

    static string Cell(object? value, int depth, HashSet<object> seen)
    {
        var text = value is null || IsScalar(value.GetType()) || value is string
            ? Format(value, depth + 1, seen)
            : value.ToString() ?? "";
        text = text.Replace('\n', ' ').Replace('\r', ' ');
        return text.Length <= MaxColumnWidth ? text : text[..(MaxColumnWidth - 1)] + "…";
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
