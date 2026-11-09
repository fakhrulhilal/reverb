// Throwaway stub of Reverb.Runtime for the directive spike. Text output only.
using System.Collections;
using System.Reflection;

namespace Reverb;

public sealed class Slider(string label, double min, double max, double value, double step)
{
    public string Label { get; } = label;
    public double Min { get; } = min;
    public double Max { get; } = max;
    public double Step { get; } = step;
    public double Value { get; set; } = value;
}

public static class Ui
{
    public static Slider Slider(string label, double min = 0, double max = 100, double value = 0, double step = 1)
        => new(label, min, max, value, step);
}

public static class DumpExtensions
{
    public static T Dump<T>(this T value, string? title = null)
    {
        if (title is not null) Console.WriteLine($"== {title} ==");
        if (value is IEnumerable seq and not string)
        {
            foreach (var item in seq) Console.WriteLine(Describe(item));
        }
        else
        {
            Console.WriteLine(Describe(value));
        }
        return value;
    }

    static string Describe(object? o)
    {
        if (o is null) return "null";
        var props = o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        if (o.GetType().IsPrimitive || o is string || props.Length == 0) return o.ToString() ?? "";
        return string.Join("  ", props.Select(p => $"{p.Name}={p.GetValue(o)}"));
    }
}
