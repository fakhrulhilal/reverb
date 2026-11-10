using System.Text.Json;

namespace Reverb.Runtime.Tests;

/// <summary>Charts are Vega-Lite specs; these check the spec and the text shown under dotnet run.</summary>
[Collection("host")]
public class ChartTests
{
    sealed record Sale(DateTime Day, string Region, double Total);

    static readonly Sale[] Sales =
    [
        new(new DateTime(2026, 1, 1), "West", 10),
        new(new DateTime(2026, 1, 2), "West", 30.5),
        new(new DateTime(2026, 1, 1), "East", 20),
    ];

    static JsonElement Spec(Chart chart) => JsonDocument.Parse(chart.Output.Data[Output.VegaLite]).RootElement;

    /// <summary>Runs with a host that records displays, so creating a chart doesn't print.</summary>
    static (T Result, List<object?> Shown) Hosted<T>(Func<T> make)
    {
        var host = new RecordingHost();
        var previous = ReverbHost.Current;
        ReverbHost.Current = host;
        try
        {
            return (make(), host.Shown);
        }
        finally
        {
            ReverbHost.Current = previous;
        }
    }

    [Fact]
    public void A_line_chart_has_the_data_and_axis_titles_from_the_lambdas()
    {
        var (chart, _) = Hosted(() => Chart.Line(Sales, x: s => s.Day, y: s => s.Total));
        var spec = Spec(chart);
        Assert.Equal("line", spec.GetProperty("mark").GetProperty("type").GetString());
        Assert.True(spec.GetProperty("mark").GetProperty("point").GetBoolean());
        var x = spec.GetProperty("encoding").GetProperty("x");
        Assert.Equal("temporal", x.GetProperty("type").GetString());
        Assert.Equal("Day", x.GetProperty("title").GetString());
        // Dates without a time of day get day ticks, not "12 PM" in between.
        Assert.Equal("yearmonthdate", x.GetProperty("timeUnit").GetString());
        var y = spec.GetProperty("encoding").GetProperty("y");
        Assert.Equal("quantitative", y.GetProperty("type").GetString());
        Assert.Equal("Total", y.GetProperty("title").GetString());
        var values = spec.GetProperty("data").GetProperty("values");
        Assert.Equal(3, values.GetArrayLength());
        Assert.Equal("2026-01-02T00:00:00", values[1].GetProperty("x").GetString());
        Assert.Equal(30.5, values[1].GetProperty("y").GetDouble());
    }

    [Fact]
    public void Times_of_day_keep_a_plain_time_axis()
    {
        var times = new[] { new DateTime(2026, 1, 1, 9, 30, 0), new DateTime(2026, 1, 1, 14, 0, 0) };
        var (chart, _) = Hosted(() => Chart.Line(times, x: t => t, y: t => t.Hour));
        Assert.False(Spec(chart).GetProperty("encoding").GetProperty("x").TryGetProperty("timeUnit", out _));
    }

    [Fact]
    public void A_chart_shows_itself_where_it_is_made()
    {
        var (chart, shown) = Hosted(() => Chart.Bar(Sales, x: s => s.Region, y: s => s.Total));
        Assert.Same(chart, Assert.Single(shown));
    }

    [Fact]
    public void Bars_keep_the_data_order_and_series_become_colours()
    {
        var (chart, _) = Hosted(() => Chart.Bar(Sales, x: s => s.Day.Day, y: s => s.Total, series: s => s.Region, title: "By day"));
        var spec = Spec(chart);
        Assert.Equal("By day", spec.GetProperty("title").GetString());
        var x = spec.GetProperty("encoding").GetProperty("x");
        Assert.Equal("ordinal", x.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, x.GetProperty("sort").ValueKind);
        var color = spec.GetProperty("encoding").GetProperty("color");
        Assert.Equal("series", color.GetProperty("field").GetString());
        Assert.Equal("Region", color.GetProperty("title").GetString());
        Assert.Equal("series", spec.GetProperty("encoding").GetProperty("xOffset").GetProperty("field").GetString());
        Assert.Equal("East", spec.GetProperty("data").GetProperty("values")[2].GetProperty("series").GetString());
    }

    [Fact]
    public void Scatter_uses_points_and_explicit_titles_win()
    {
        var (chart, _) = Hosted(() => Chart.Scatter(Sales, x: s => s.Total, y: s => s.Total * 2, xTitle: "Sales", yTitle: "Double"));
        var spec = Spec(chart);
        Assert.Equal("point", spec.GetProperty("mark").GetProperty("type").GetString());
        Assert.Equal("Sales", spec.GetProperty("encoding").GetProperty("x").GetProperty("title").GetString());
        Assert.Equal("Double", spec.GetProperty("encoding").GetProperty("y").GetProperty("title").GetString());
    }

    [Fact]
    public void A_lambda_that_is_not_a_plain_member_gets_its_text_as_the_title()
    {
        var (chart, _) = Hosted(() => Chart.Scatter(Sales, x: s => s.Total, y: s => s.Total * 2));
        Assert.Equal("s.Total * 2", Spec(chart).GetProperty("encoding").GetProperty("y").GetProperty("title").GetString());
    }

    [Fact]
    public void The_text_form_says_what_the_chart_holds()
    {
        var (chart, _) = Hosted(() => Chart.Line(Sales, x: s => s.Day, y: s => s.Total, title: "Sales"));
        Assert.Equal("[line chart \"Sales\": 3 points; Day 2026-01-01 to 2026-01-02; Total 10 to 30.5]", chart.Output.Text);
    }

    [Fact]
    public void Under_dotnet_run_a_chart_prints_its_summary()
    {
        var output = new StringWriter();
        var previous = Console.Out;
        Console.SetOut(output);
        try
        {
            Chart.Bar(Sales, x: s => s.Region, y: s => s.Total);
        }
        finally
        {
            Console.SetOut(previous);
        }
        Assert.Equal("[bar chart: 3 points; Region East to West; Total 10 to 30.5]\n", output.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Large_data_is_cut_with_a_note()
    {
        var many = Enumerable.Range(0, Chart.MaxPoints + 10).Select(i => new Sale(DateTime.UnixEpoch.AddDays(i), "X", i));
        var (chart, _) = Hosted(() => Chart.Line(many, x: s => s.Day, y: s => s.Total));
        Assert.Equal(Chart.MaxPoints, Spec(chart).GetProperty("data").GetProperty("values").GetArrayLength());
        Assert.False(Spec(chart).GetProperty("mark").GetProperty("point").GetBoolean()); // too many to mark each one
        Assert.Contains("first 5,000 of 5,010 points", chart.Output.Text);
    }

    [Fact]
    public void Raw_vega_lite_is_passed_through_and_bad_json_is_refused()
    {
        var (chart, _) = Hosted(() => Chart.VegaLite("""{"mark":"bar","data":{"values":[{"a":1}]}}"""));
        Assert.Equal("bar", Spec(chart).GetProperty("mark").GetString());
        Assert.Equal("[chart]", chart.Output.Text);

        var e = Assert.Throws<ArgumentException>(() => Hosted(() => Chart.VegaLite("{ not json")));
        Assert.Contains("isn't valid JSON", e.Message);
        e = Assert.Throws<ArgumentException>(() => Hosted(() => Chart.VegaLite("[1, 2]")));
        Assert.Contains("JSON object", e.Message);
    }

    [Fact]
    public void Nulls_and_odd_values_are_written_safely()
    {
        var rows = new[] { (Name: (string?)null, Value: double.NaN), (Name: "b", Value: 2.0) };
        var (chart, _) = Hosted(() => Chart.Bar(rows, x: r => r.Name, y: r => r.Value));
        var values = Spec(chart).GetProperty("data").GetProperty("values");
        Assert.Equal(JsonValueKind.Null, values[0].GetProperty("x").ValueKind);
        Assert.Equal(JsonValueKind.Null, values[0].GetProperty("y").ValueKind);
    }

    sealed class RecordingHost : ReverbHost
    {
        public List<object?> Shown { get; } = [];
        public override void Display(object? value, string? title) => Shown.Add(value);
        public override void Markdown(string markdown) => Shown.Add(markdown);
        public override void Html(string html) => Shown.Add(html);
    }
}

[CollectionDefinition("host", DisableParallelization = true)]
public sealed class HostCollection;
