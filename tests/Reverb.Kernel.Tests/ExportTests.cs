using System.Text.Json;
using System.Text.RegularExpressions;
using Reverb.Testing;

namespace Reverb.Kernel.Tests;

/// <summary>reverb export --html: one page, nothing fetched from anywhere.</summary>
[Collection("cli")]
public class ExportTests(ITestOutputHelper output)
{
    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reverb-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Directory.Build.props"), "<Project />");
        return dir;
    }

    static string Fixture(string name, string dir)
    {
        var target = Path.Combine(dir, name + ".cs");
        File.Copy(Path.Combine(Snapshot.FixturesDirectory, name + ".cs"), target);
        return target;
    }

    static JsonElement Notebook(string html)
    {
        var m = Regex.Match(html, "<script type=\"application/json\" id=\"reverb-notebook\">(.*?)</script>", RegexOptions.Singleline);
        Assert.True(m.Success, "the page has no notebook data");
        return JsonDocument.Parse(m.Groups[1].Value).RootElement;
    }

    [Fact]
    public async Task A_notebook_with_charts_exports_to_one_page_with_everything_inlined()
    {
        var dir = TempDir();
        var file = Fixture("charts", dir);
        var r = await Cli.Reverb(dir, "export", file, "--html");
        output.WriteLine(r.Stderr);
        Assert.Equal(0, r.ExitCode);
        var html = await File.ReadAllTextAsync(Path.ChangeExtension(file, ".html"));

        Assert.Contains("<title>Charts</title>", html);
        Assert.Contains("reverbVegaEmbed=", html); // the chart library sets this
        // Nothing loaded from elsewhere.
        Assert.DoesNotMatch("<script[^>]+src=", html);
        Assert.DoesNotContain("<link", html);

        var notebook = Notebook(html);
        Assert.Equal("charts.cs", notebook.GetProperty("name").GetString());
        var line = notebook.GetProperty("cells").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "line");
        Assert.Equal("idle", line.GetProperty("status").GetString());
        Assert.StartsWith("Chart.Line(", line.GetProperty("source").GetString());
        Assert.True(line.GetProperty("outputs")[0].GetProperty("data").TryGetProperty("application/vnd.vegalite.v5+json", out _));
        Assert.Contains("Wrote ", r.Stderr);
    }

    [Fact]
    public async Task Without_charts_the_chart_library_is_left_out_and_no_code_drops_the_code()
    {
        var dir = TempDir();
        var file = Fixture("sales", dir);
        var target = Path.Combine(dir, "out", "page.html");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var r = await Cli.Reverb(dir, "export", file, "--html", "-o", target, "--no-code");
        output.WriteLine(r.Stderr);
        Assert.Equal(0, r.ExitCode);
        var html = await File.ReadAllTextAsync(target);
        Assert.DoesNotContain("reverbVegaEmbed=", html); // no chart library
        var cells = Notebook(html).GetProperty("cells").EnumerateArray().ToList();
        Assert.DoesNotContain(cells, c => c.GetProperty("kind").GetString() is "setup" or "types");
        Assert.All(cells.Where(c => c.GetProperty("kind").GetString() == "code"), c => Assert.False(c.TryGetProperty("source", out _)));
        Assert.Contains(cells, c => c.GetProperty("kind").GetString() == "markdown" && c.GetProperty("source").GetString()!.Contains("Sales explorer"));
    }

    [Fact]
    public async Task A_failing_cell_is_still_exported_and_the_exit_code_says_so()
    {
        var dir = TempDir();
        var file = Path.Combine(dir, "broken.cs");
        await File.WriteAllTextAsync(file, NotebookText.Build(["var a = 1;", "throw new InvalidOperationException(\"</script><b>nope</b>\");"]));
        var r = await Cli.Reverb(dir, "export", file, "--html");
        output.WriteLine(r.Stderr);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("1 cell failed (c2)", r.Stderr);
        var html = await File.ReadAllTextAsync(Path.ChangeExtension(file, ".html"));
        // The message can't close the data's script tag.
        Assert.DoesNotContain("</script><b>", html);
        var c2 = Notebook(html).GetProperty("cells").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "c2");
        Assert.Equal("error", c2.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Export_needs_a_format_and_won_t_overwrite_the_notebook()
    {
        var dir = TempDir();
        var file = Fixture("hello", dir);
        var r = await Cli.Reverb(dir, "export", file);
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("--html", r.Stderr);
        r = await Cli.Reverb(dir, "export", file, "--html", "-o", file);
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("overwrite the notebook", r.Stderr);
    }
}
