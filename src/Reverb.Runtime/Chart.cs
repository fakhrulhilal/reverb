using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Reverb;

/// <summary>
/// A chart. It shows where it's made, like a widget: <c>Chart.Line(sales, x: s =&gt; s.Day, y: s =&gt; s.Total);</c>
/// is enough. In the editor it's drawn from a Vega-Lite spec; under <c>dotnet run</c> it prints a
/// one-line summary.
/// </summary>
public sealed class Chart
{
    /// <summary>More rows than this are cut off (with a note), to keep the page responsive.</summary>
    public const int MaxPoints = 5000;

    /// <summary>Line charts mark each point up to this many; past that the marks just thicken the line.</summary>
    public const int MaxMarkedPoints = 60;

    Chart(Output output) => Output = output;

    /// <summary>The chart as an output: Vega-Lite JSON plus a text summary.</summary>
    public Output Output { get; }

    public override string ToString() => Output.Text;

    /// <summary>A line per series, with points marked. Good for values over time.</summary>
    public static Chart Line<T, TX, TY>(IEnumerable<T> data, Func<T, TX> x, Func<T, TY> y, Func<T, object?>? series = null,
        string? title = null, string? xTitle = null, string? yTitle = null,
        [CallerArgumentExpression(nameof(x))] string? xExpression = null,
        [CallerArgumentExpression(nameof(y))] string? yExpression = null,
        [CallerArgumentExpression(nameof(series))] string? seriesExpression = null) =>
        Show(Build("line", data, x, y, series, title, xTitle ?? Title(xExpression), yTitle ?? Title(yExpression), Title(seriesExpression)));

    /// <summary>Bars in the order of the data. With <paramref name="series"/>, bars are grouped side by side.</summary>
    public static Chart Bar<T, TX, TY>(IEnumerable<T> data, Func<T, TX> x, Func<T, TY> y, Func<T, object?>? series = null,
        string? title = null, string? xTitle = null, string? yTitle = null,
        [CallerArgumentExpression(nameof(x))] string? xExpression = null,
        [CallerArgumentExpression(nameof(y))] string? yExpression = null,
        [CallerArgumentExpression(nameof(series))] string? seriesExpression = null) =>
        Show(Build("bar", data, x, y, series, title, xTitle ?? Title(xExpression), yTitle ?? Title(yExpression), Title(seriesExpression)));

    /// <summary>One point per row.</summary>
    public static Chart Scatter<T, TX, TY>(IEnumerable<T> data, Func<T, TX> x, Func<T, TY> y, Func<T, object?>? series = null,
        string? title = null, string? xTitle = null, string? yTitle = null,
        [CallerArgumentExpression(nameof(x))] string? xExpression = null,
        [CallerArgumentExpression(nameof(y))] string? yExpression = null,
        [CallerArgumentExpression(nameof(series))] string? seriesExpression = null) =>
        Show(Build("point", data, x, y, series, title, xTitle ?? Title(xExpression), yTitle ?? Title(yExpression), Title(seriesExpression)));

    /// <summary>Any Vega-Lite spec, for charts the helpers above don't cover. See https://vega.github.io/vega-lite/.</summary>
    public static Chart VegaLite(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("A Vega-Lite spec is a JSON object ({ ... }), but this is a " + doc.RootElement.ValueKind.ToString().ToLowerInvariant() + ".", nameof(json));
        }
        catch (JsonException e)
        {
            throw new ArgumentException($"This Vega-Lite spec isn't valid JSON: {e.Message}", nameof(json), e);
        }
        return Show(new Chart(new Output(new Dictionary<string, string> { [Output.PlainText] = "[chart]", [Output.VegaLite] = json })));
    }

    static Chart Show(Chart chart)
    {
        ReverbHost.Effective.Display(chart, null);
        return chart;
    }

    // "s => s.Day" becomes "Day"; anything else keeps its body text ("s.Total * 2").
    static readonly Regex Lambda = new(@"^\s*\(?\s*(?<p>\w+)\s*\)?\s*=>\s*(?<body>.+?)\s*$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    static readonly Regex MemberPath = new(@"^\w+(\.\w+)+$", RegexOptions.CultureInvariant);

    static string? Title(string? expression)
    {
        if (expression is null || expression == "null") return null;
        var m = Lambda.Match(expression);
        if (!m.Success) return expression.Trim();
        var body = m.Groups["body"].Value;
        return MemberPath.IsMatch(body) && body.StartsWith(m.Groups["p"].Value + ".", StringComparison.Ordinal)
            ? body[(body.LastIndexOf('.') + 1)..]
            : body;
    }

    static Chart Build<T, TX, TY>(string mark, IEnumerable<T> data, Func<T, TX> x, Func<T, TY> y, Func<T, object?>? series,
        string? title, string? xTitle, string? yTitle, string? seriesTitle)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);

        var rows = new List<(object? X, object? Y, object? Series)>();
        var total = 0;
        foreach (var item in data)
        {
            total++;
            if (rows.Count < MaxPoints) rows.Add((x(item), y(item), series?.Invoke(item)));
        }

        var xKind = Kind(typeof(TX), rows.Select(r => r.X));
        var xType = xKind switch
        {
            ValueKind.Time => "temporal",
            ValueKind.Number when mark != "bar" => "quantitative",
            ValueKind.Number => "ordinal",
            _ => "nominal",
        };

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            if (title is not null) w.WriteString("title", title);
            w.WriteString("width", "container");
            w.WriteNumber("height", 280);
            w.WriteStartObject("data");
            w.WriteStartArray("values");
            foreach (var (vx, vy, vs) in rows)
            {
                w.WriteStartObject();
                w.WritePropertyName("x");
                WriteValue(w, vx);
                w.WritePropertyName("y");
                WriteValue(w, vy);
                if (series is not null)
                {
                    w.WritePropertyName("series");
                    WriteValue(w, vs);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartObject("mark");
            w.WriteString("type", mark);
            w.WriteBoolean("tooltip", true);
            if (mark == "line") w.WriteBoolean("point", rows.Count <= MaxMarkedPoints);
            if (mark == "point") w.WriteBoolean("filled", true);
            w.WriteEndObject();

            w.WriteStartObject("encoding");
            w.WriteStartObject("x");
            w.WriteString("field", "x");
            w.WriteString("type", xType);
            if (xTitle is not null) w.WriteString("title", xTitle);
            // Whole days: ticks per day, not "12 PM" in between.
            if (xKind == ValueKind.Time && rows.All(r => r.X is null or DateOnly || r.X is DateTime { TimeOfDay.Ticks: 0 }))
                w.WriteString("timeUnit", "yearmonthdate");
            if (mark == "bar") w.WriteNull("sort"); // keep the data's order
            w.WriteEndObject();
            w.WriteStartObject("y");
            w.WriteString("field", "y");
            w.WriteString("type", "quantitative");
            if (yTitle is not null) w.WriteString("title", yTitle);
            w.WriteEndObject();
            if (series is not null)
            {
                w.WriteStartObject("color");
                w.WriteString("field", "series");
                w.WriteString("type", "nominal");
                if (seriesTitle is not null) w.WriteString("title", seriesTitle);
                w.WriteEndObject();
                if (mark == "bar")
                {
                    w.WriteStartObject("xOffset");
                    w.WriteString("field", "series");
                    w.WriteEndObject();
                }
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }

        var text = Summary(mark, title, rows, total, xTitle ?? "x", yTitle ?? "y");
        return new Chart(new Output(new Dictionary<string, string>
        {
            [Output.PlainText] = text,
            [Output.VegaLite] = Encoding.UTF8.GetString(stream.ToArray()),
        }));
    }

    enum ValueKind { Other, Number, Time }

    static ValueKind Kind(Type declared, IEnumerable<object?> values)
    {
        var type = Nullable.GetUnderlyingType(declared) ?? declared;
        if (type == typeof(object)) type = values.FirstOrDefault(v => v is not null)?.GetType() ?? type;
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly)) return ValueKind.Time;
        return IsNumber(type) ? ValueKind.Number : ValueKind.Other;
    }

    static bool IsNumber(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(double) || t == typeof(float) || t == typeof(decimal)
        || t == typeof(short) || t == typeof(byte) || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte);

    static void WriteValue(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null:
                w.WriteNullValue();
                break;
            case string s:
                w.WriteStringValue(s);
                break;
            case bool b:
                w.WriteBooleanValue(b);
                break;
            case double d when !double.IsFinite(d):
            case float f when !float.IsFinite(f):
                w.WriteNullValue(); // JSON has no NaN or infinity
                break;
            case DateTime or DateTimeOffset or DateOnly:
                w.WriteStringValue(TimeText(value, full: true));
                break;
            case decimal m:
                w.WriteNumberValue(m);
                break;
            case IConvertible c when IsNumber(value.GetType()):
                w.WriteNumberValue(c.ToDouble(CultureInfo.InvariantCulture));
                break;
            default:
                w.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    static string TimeText(object value, bool full) => value switch
    {
        DateTime d when !full && d.TimeOfDay == TimeSpan.Zero => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.ToString(full ? "O" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>"[line chart "Sales": 3 points; Day 2026-01-01 to 2026-01-02; Total 10 to 30.5]"</summary>
    static string Summary(string mark, string? title, List<(object? X, object? Y, object? Series)> rows, int total, string xTitle, string yTitle)
    {
        var kind = mark == "point" ? "scatter" : mark;
        var points = rows.Count == total
            ? $"{total.ToString("N0", CultureInfo.InvariantCulture)} point{(total == 1 ? "" : "s")}"
            : $"first {rows.Count.ToString("N0", CultureInfo.InvariantCulture)} of {total.ToString("N0", CultureInfo.InvariantCulture)} points";
        var parts = new List<string> { points };
        if (Range(rows.Select(r => r.X)) is { } xr) parts.Add($"{xTitle} {xr}");
        if (Range(rows.Select(r => r.Y)) is { } yr) parts.Add($"{yTitle} {yr}");
        return $"[{kind} chart{(title is null ? "" : $" \"{title}\"")}: {string.Join("; ", parts)}]";
    }

    static string? Range(IEnumerable<object?> values)
    {
        var present = values.Where(v => v is not null && !(v is double d && !double.IsFinite(d))).ToList();
        if (present.Count == 0) return null;
        if (present.All(v => v is IComparable && IsNumber(v!.GetType())))
        {
            var numbers = present.Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToList();
            return $"{numbers.Min().ToString(CultureInfo.InvariantCulture)} to {numbers.Max().ToString(CultureInfo.InvariantCulture)}";
        }
        var texts = present.Select(v => v is DateTime or DateTimeOffset or DateOnly ? TimeText(v!, full: false) : Convert.ToString(v, CultureInfo.InvariantCulture) ?? "")
            .Order(StringComparer.Ordinal).ToList();
        return texts[0] == texts[^1] ? texts[0] : $"{texts[0]} to {texts[^1]}";
    }
}
