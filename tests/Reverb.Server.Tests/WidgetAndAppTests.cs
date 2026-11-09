using Reverb.Testing;

namespace Reverb.Server.Tests;

[Collection("server")]
public class WidgetAndAppTests
{
    // The sales explorer sample notebook.
    const string Sales = """
        #!/usr/bin/env dotnet
        #:package Reverb.Runtime@0.1.0
        // reverb-notebook v1

        // %% setup
        using Reverb;

        // %% [markdown]
        // # Sales explorer
        // Drag the slider to change the minimum order size.

        // %% id=data
        var rng = new Random(42);
        string[] regions = ["West", "East", "Central"];
        var orders = Enumerable.Range(1, 500)
            .Select(i => new Order(i, regions[i % 3], Math.Round(rng.NextDouble() * 1000, 2)))
            .ToList();

        // %% id=threshold
        var threshold = Ui.Slider("Minimum order", min: 0, max: 1000, value: 400);

        // %% id=summary
        var big = orders.Where(o => o.Total >= threshold.Value).ToList();
        big.GroupBy(o => o.Region)
           .Select(g => new { Region = g.Key, Count = g.Count(), Revenue = g.Sum(o => o.Total) })
           .Dump("Orders above threshold");

        // %% types
        record Order(int Id, string Region, double Total);

        """;

    static int TotalCount(TestClient client)
    {
        var json = client["summary"].Outputs.Single().Data![Output.Table];
        using var table = System.Text.Json.JsonDocument.Parse(json);
        return table.RootElement.GetProperty("rows").EnumerateArray().Sum(r => int.Parse(r[1].GetString()!));
    }

    [Fact]
    public async Task Moving_the_slider_reruns_only_its_readers()
    {
        await using var server = await TestServer.StartAsync(Sales);
        await using var client = await server.ConnectAsync();
        await client.WaitFor(() => client.Status("summary") == "idle" && client["summary"].Outputs.Count == 1, what: "the first run");
        var atDefault = TotalCount(client);
        Assert.Equal((1, 1, 1), (client.Runs("data"), client.Runs("threshold"), client.Runs("summary")));

        await client.SetWidget("threshold:0", 900, "slider");
        await client.WaitFor(() => client.Runs("summary") == 2 && client.Status("summary") == "idle", what: "summary to re-run");
        await server.Host.IdleAsync();
        Assert.Equal((1, 1), (client.Runs("data"), client.Runs("threshold")));
        Assert.True(TotalCount(client) < atDefault, $"{TotalCount(client)} orders at 900, {atDefault} at 400");

        // The slider's own output shows the new value without its cell running.
        await client.WaitFor(() => client.Widget("threshold")?.GetProperty("value").GetDouble() == 900, what: "the slider to redraw");
    }

    [Fact]
    public async Task A_burst_of_slider_moves_runs_the_reader_far_fewer_times_and_ends_on_the_last_value()
    {
        var text = NotebookText.Build([
            "var s = Ui.Slider(\"s\", min: 0, max: 100, value: 0);",
            "Thread.Sleep(150);\nConsole.WriteLine(s.Value);",
        ]);
        await using var server = await TestServer.StartAsync(text);
        await using var client = await server.ConnectAsync();
        await client.WaitFor(() => client.Stdout("c2") == "0\n");

        for (var v = 1; v <= 30; v++) await client.SetWidget("c1:0", v, "slider");
        await client.WaitFor(() => client.Stdout("c2") == "30\n" && client.Status("c2") == "idle", what: "the last value");
        await Task.Delay(500);
        Assert.Equal("30\n", client.Stdout("c2"));
        Assert.InRange(client.Runs("c2"), 2, 10);
    }

    [Fact]
    public async Task Every_button_click_counts()
    {
        var text = NotebookText.Build([
            "var go = Ui.Button(\"Go\");",
            "Thread.Sleep(100);\nConsole.WriteLine(go.Value);",
        ]);
        await using var server = await TestServer.StartAsync(text);
        await using var client = await server.ConnectAsync();
        await client.WaitFor(() => client.Stdout("c2") == "0\n");
        for (var i = 0; i < 4; i++) await client.SetWidget("c1:0", true, "button");
        await client.WaitFor(() => client.Stdout("c2") == "4\n" && client.Status("c2") == "idle", what: "four clicks");
    }

    [Fact]
    public async Task Every_widget_kind_works_over_the_socket()
    {
        var text = NotebookText.Build([
            """
            var region = Ui.Dropdown("Region", ["West", "East"]);
            var query = Ui.Text("Search");
            var all = Ui.Checkbox("All");
            var rows = Ui.Number("Rows", 10, min: 1, max: 100);
            """,
            "Console.WriteLine($\"{region.Value} {query.Value} {all.Value} {rows.Value}\");",
        ]);
        await using var server = await TestServer.StartAsync(text);
        await using var client = await server.ConnectAsync();
        await client.WaitFor(() => client.Stdout("c2") == "West  False 10\n");
        await client.SetWidget("c1:0", 1, "dropdown");
        await client.SetWidget("c1:1", "tea", "text");
        await client.SetWidget("c1:2", true, "checkbox");
        await client.SetWidget("c1:3", 25, "number");
        await client.WaitFor(() => client.Stdout("c2") == "East tea True 25\n", what: "all four changes");
    }

    [Fact]
    public async Task App_mode_shows_outputs_but_no_code_and_cant_be_edited()
    {
        await using var server = await TestServer.StartAsync(Sales.Replace("// reverb-notebook v1", "// reverb-notebook v1 lazy"), appMode: true);
        var original = server.FileText();
        await using var client = await server.ConnectAsync();
        Assert.Equal("app", client.Mode);
        Assert.False(client.Lazy);

        // Lazy is ignored in an app: everything runs, and widget changes re-run readers.
        await client.WaitFor(() => client.Status("summary") == "idle" && client["summary"].Outputs.Count == 1, what: "the app to run");
        Assert.DoesNotContain(client.CellIds(), id => id is "setup" or "types");
        Assert.All(client.Cells.Where(c => c.Kind == "code"), c => Assert.Equal("", c.Source));
        Assert.Contains("# Sales explorer", client.Cells.Single(c => c.Kind == "markdown").Source);
        Assert.Empty(client.Edges);

        await client.SetWidget("threshold:0", 950, "slider");
        await client.WaitFor(() => client.Runs("summary") == 2 && client.Status("summary") == "idle", what: "the reader to re-run");

        foreach (var m in new ClientMessage[]
        {
            new() { Type = BrowserMessages.CellUpdate, CellId = "data", Source = "var orders = 1;" },
            new() { Type = BrowserMessages.CellRun, CellId = "data" },
            new() { Type = BrowserMessages.CellCreate, AfterCellId = "data", Kind = "code" },
            new() { Type = BrowserMessages.CellDelete, CellId = "summary" },
            new() { Type = BrowserMessages.SetLazy, Lazy = true },
            new() { Type = BrowserMessages.KernelRestart },
            new() { Type = BrowserMessages.Save },
        })
            await client.SendAsync(m);
        await client.WaitFor(() => client.Messages.Count(m => m.Type == BrowserMessages.Error) == 7, what: "every edit to be refused");
        await server.Host.IdleAsync();
        Assert.Equal(1, client.Runs("data"));
        Assert.Equal(original, server.FileText());
    }

    [Fact]
    public async Task An_app_reruns_after_its_kernel_dies()
    {
        await using var server = await TestServer.StartAsync(NotebookText.Build(["Console.WriteLine(\"up\");"]), appMode: true);
        await using var client = await server.ConnectAsync();
        await client.WaitFor(() => client.Runs("c1") == 1 && client.Status("c1") == "idle");
        var pid = server.Host.KernelProcessId!.Value;
        System.Diagnostics.Process.GetProcessById(pid).Kill();
        await client.WaitFor(() => client.Runs("c1") == 2 && client.Status("c1") == "idle" && server.Host.KernelProcessId != pid, what: "the app to run again");
        Assert.Equal("up\n", client.Stdout("c1"));
    }

    [Fact]
    public async Task A_kernel_that_keeps_dying_is_left_dead()
    {
        await using var server = await TestServer.StartAsync(NotebookText.Build(["Environment.Exit(4);"]), appMode: true);
        await using var client = await server.ConnectAsync();
        await client.WaitFor(() => client.Messages.Any(m => m.Type == BrowserMessages.KernelStatus && m.State == "dead" && m.Message!.Contains("three times")), seconds: 60, what: "Reverb to give up");
        await Task.Delay(1000);
        Assert.Equal("dead", client.KernelState);
    }
}
