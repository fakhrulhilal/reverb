using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using Reverb.Format;
using Reverb.Testing;

namespace Reverb.Server.Tests;

[CollectionDefinition("server", DisableParallelization = true)]
public class ServerCollection;

[Collection("server")]
public class ServerTests(ITestOutputHelper output)
{
    static readonly string Chain = NotebookText.Build(["var a = 1;", "var b = a * 10;", "Console.WriteLine(b + 1);"]);

    [Fact]
    public async Task Connecting_gets_a_snapshot_with_outputs_from_the_first_run()
    {
        await using var server = await TestServer.StartAsync(Chain);
        await using var client = await server.ConnectAsync();
        await client.WaitFor(() => client.Stdout("c3") == "11\n", what: "the first run");
        Assert.Equal(["setup", "c1", "c2", "c3"], client.CellIds());
        Assert.Contains(client.Edges, e => e.From == "c3" && e.To == "c2" && e.Via.SequenceEqual(["b"]));

        // A second tab sees the same state straight away.
        await using var second = await server.ConnectAsync();
        Assert.Equal("11\n", second.Stdout("c3"));
        Assert.Equal("idle", second.Status("c3"));
    }

    [Fact]
    public async Task Typing_saves_but_does_not_run_until_the_cell_runs()
    {
        await using var server = await TestServer.StartAsync(Chain);
        await using var client = await server.ConnectAsync();
        await client.WaitFor(() => client.Stdout("c3") == "11\n");

        await client.Update("c1", "var a = 5;");
        await client.WaitFor(() => server.FileText().Contains("var a = 5;"), what: "autosave");
        Assert.Equal("11\n", client.Stdout("c3"));
        // The save tells the tab which of its messages it covers.
        await client.WaitFor(() => client.Messages.Any(m => m.Type == BrowserMessages.FileSaved && m.Ack == 1), what: "the save to acknowledge the edit");

        await client.Run("c1");
        await client.WaitFor(() => client.Stdout("c3") == "51\n", what: "the reactive run");
        Assert.False(client["c1"].Edited);
    }

    [Fact]
    public async Task Edited_cells_keep_their_old_code_in_the_kernel_until_run()
    {
        await using var server = await TestServer.StartAsync(Chain);
        await using var client = await server.ConnectAsync();
        await client.WaitFor(() => client.Stdout("c3") == "11\n");

        // c1 is edited but not run; running c3 still uses the a the kernel has.
        await client.Update("c1", "var a = 7;");
        await client.Run("c3");
        await client.WaitFor(() => client.Messages.Count(m => m.Type == BrowserMessages.CellStatus && m.CellId == "c3" && m.Status == "running") == 2, what: "c3 to run again");
        await client.WaitFor(() => client.Status("c3") == "idle");
        Assert.Equal("11\n", client.Stdout("c3"));
        await server.Host.IdleAsync();
        await using (var other = await server.ConnectAsync())
            Assert.True(other["c1"].Edited);

        await client.SendAsync(new ClientMessage { Type = BrowserMessages.RunAll });
        await client.WaitFor(() => client.Stdout("c3") == "71\n", what: "run all");
        await server.Host.IdleAsync();
        await using (var other = await server.ConnectAsync())
            Assert.False(other["c1"].Edited);
    }

    [Fact]
    public async Task Diagnostics_follow_typing()
    {
        await using var server = await TestServer.StartAsync(Chain);
        await using var client = await server.ConnectAsync();
        await client.Update("c2", "var b = a * missing;");
        await client.WaitFor(() => client["c2"].Diagnostics.Any(d => d.Id == "CS0103"), what: "a diagnostic");
        var d = client["c2"].Diagnostics.Single(d => d.Id == "CS0103");
        Assert.Equal((0, 12), (d.StartLine, d.StartColumn));
        await client.Update("c2", "var b = a * 2;");
        await client.WaitFor(() => client["c2"].Diagnostics.Count == 0, what: "the diagnostic to clear");
    }

    [Fact]
    public async Task Editing_the_file_on_disk_updates_the_editor_and_reruns()
    {
        await using var server = await TestServer.StartAsync(Chain);
        await using var client = await server.ConnectAsync();
        await client.WaitFor(() => client.Stdout("c3") == "11\n");

        await File.WriteAllTextAsync(server.NotebookPath, server.FileText().Replace("var a = 1;", "var a = 3;"));
        await client.WaitFor(() => client["c1"].Source == "var a = 3;", what: "the new source");
        await client.WaitFor(() => client.Stdout("c3") == "31\n", what: "the rerun");
        Assert.True(client.Has(BrowserMessages.FileExternalChange));
    }

    [Fact]
    public async Task An_external_edit_that_adds_an_unmarked_cell_is_picked_up()
    {
        await using var server = await TestServer.StartAsync(Chain);
        await using var client = await server.ConnectAsync();
        await client.WaitFor(() => client.Stdout("c3") == "11\n");

        // Like an agent appending a cell without an id.
        await File.AppendAllTextAsync(server.NotebookPath, "\n// %%\nConsole.WriteLine(b * 2);\n");
        await client.WaitFor(() => client.CellIds().Count == 5, what: "the new cell");
        var added = client.CellIds()[^1];
        await client.WaitFor(() => client.Stdout(added) == "20\n", what: "the new cell to run");
        Assert.Equal("11\n", client.Stdout("c3"));
    }

    [Fact]
    public async Task Our_own_saves_are_not_mistaken_for_external_changes()
    {
        await using var server = await TestServer.StartAsync(Chain);
        await using var client = await server.ConnectAsync();
        await client.Update("c1", "var a = 2;");
        await client.WaitFor(() => client.Has(BrowserMessages.FileSaved), what: "a save");
        await Task.Delay(1500); // longer than the watcher's poll
        Assert.False(client.Has(BrowserMessages.FileExternalChange));
    }

    [Fact]
    public async Task Structural_changes_are_saved_at_once()
    {
        await using var server = await TestServer.StartAsync(Chain);
        await using var client = await server.ConnectAsync();
        await client.WaitFor(() => client.Stdout("c3") == "11\n");

        await client.SendAsync(new ClientMessage { Type = BrowserMessages.CellCreate, CellId = "added", AfterCellId = "c3", Kind = "code" });
        await client.WaitFor(() => client.CellIds().Contains("added"));
        Assert.Contains("// %% id=added", server.FileText());

        await client.SendAsync(new ClientMessage { Type = BrowserMessages.CellMove, CellId = "c3", AfterCellId = "added" });
        await client.WaitFor(() => client.CellIds().SequenceEqual(["setup", "c1", "c2", "added", "c3"]));
        Assert.True(server.FileText().IndexOf("id=added", StringComparison.Ordinal) < server.FileText().IndexOf("id=c3", StringComparison.Ordinal));

        // Deleting a cell removes its variables: its readers fail.
        await client.SendAsync(new ClientMessage { Type = BrowserMessages.CellDelete, CellId = "c2" });
        await client.WaitFor(() => client.Status("c3") == "error", what: "c3 to lose its input");
        Assert.DoesNotContain("id=c2", server.FileText());

        await client.SendAsync(new ClientMessage { Type = BrowserMessages.CellSetAttrs, CellId = "c1", Attrs = new CellAttrs { HideCode = true, Name = "start" } });
        await client.WaitFor(() => server.FileText().Contains("// %% id=c1 hide_code name=start"), what: "the attributes in the file");
    }

    [Fact]
    public async Task Setup_and_types_cells_stay_in_place()
    {
        await using var server = await TestServer.StartAsync(NotebookText.Build(["var p = new P(1);"], types: "record P(int X);"));
        await using var client = await server.ConnectAsync();
        await client.SendAsync(new ClientMessage { Type = BrowserMessages.CellCreate, CellId = "first", AfterCellId = null, Kind = "code" });
        await client.SendAsync(new ClientMessage { Type = BrowserMessages.CellCreate, CellId = "last", AfterCellId = "types", Kind = "markdown" });
        await client.SendAsync(new ClientMessage { Type = BrowserMessages.CellDelete, CellId = "setup" });
        await client.WaitFor(() => client.CellIds().SequenceEqual(["setup", "first", "c1", "last", "types"]), what: "cells in order");
        await client.WaitFor(() => client.Has(BrowserMessages.Error));
    }

    [Fact]
    public async Task Lazy_mode_is_stored_in_the_header()
    {
        await using var server = await TestServer.StartAsync(Chain);
        await using var client = await server.ConnectAsync();
        await client.WaitFor(() => client.Stdout("c3") == "11\n");
        await client.SendAsync(new ClientMessage { Type = BrowserMessages.SetLazy, Lazy = true });
        await client.WaitFor(() => client.Lazy && server.FileText().Contains("// reverb-notebook v1 lazy"));

        await client.Update("c1", "var a = 4;");
        await client.Run("c1");
        await client.WaitFor(() => client.Status("c1") == "idle" && client.Status("c2") == "stale" && client.Status("c3") == "stale", what: "dependents to go stale");
        Assert.Equal("11\n", client.Stdout("c3"));

        await client.Run("c3");
        await client.WaitFor(() => client.Stdout("c3") == "41\n", what: "c3 and its stale input to run");
    }

    [Fact]
    public async Task Restart_stops_a_runaway_cell_and_the_notebook_recovers()
    {
        var text = NotebookText.Build(["var a = 1;", "var spin = 0;\nwhile (true) { spin++; }", "Console.WriteLine(a + 1);"]);
        await using var server = await TestServer.StartAsync(text);
        await using var client = await server.ConnectAsync();
        await client.WaitFor(() => client.Status("c2") == "running", what: "the loop to start");
        var pid = server.Host.KernelProcessId!.Value;

        await client.SendAsync(new ClientMessage { Type = BrowserMessages.KernelRestart });
        await client.WaitFor(() => client.KernelState == "ready" && server.Host.KernelProcessId != pid, what: "a new kernel");
        Assert.True(HasExited(pid));
        Assert.All(new[] { "c1", "c2", "c3" }, c => Assert.Equal("stale", client.Status(c)));

        await client.Update("c2", "var spin = 0;");
        await client.Run("c3");
        await client.WaitFor(() => client.Stdout("c3") == "2\n", what: "c3 to run with its stale input");
        Assert.Equal("stale", client.Status("c2"));
    }

    [Fact]
    public async Task A_killed_kernel_is_replaced()
    {
        await using var server = await TestServer.StartAsync(Chain);
        await using var client = await server.ConnectAsync();
        await client.WaitFor(() => client.Stdout("c3") == "11\n");
        var pid = server.Host.KernelProcessId!.Value;

        Process.GetProcessById(pid).Kill();
        await client.WaitFor(() => client.Messages.Any(m => m.Type == BrowserMessages.KernelStatus && m.State == "dead"), what: "the kernel to be reported dead");
        await client.WaitFor(() => client.KernelState == "ready" && server.Host.KernelProcessId != pid, what: "a new kernel");

        await client.SendAsync(new ClientMessage { Type = BrowserMessages.RunAll });
        await client.WaitFor(() => client.Status("c3") == "idle" && client.Stdout("c3") == "11\n", what: "the notebook to run again");
    }

    [Fact]
    public async Task Kernel_is_a_plain_file_based_app_without_aspnet()
    {
        var probe = "Console.WriteLine(Type.GetType(\"Microsoft.AspNetCore.Http.HttpContext, Microsoft.AspNetCore.Http.Abstractions\") is null);";
        await using var server = await TestServer.StartAsync(NotebookText.Build([probe]));
        await using var client = await server.ConnectAsync();
        await client.WaitFor(() => client.Stdout("c1") != "", what: "the probe");
        Assert.Equal("True\n", client.Stdout("c1"));
    }

    [Fact]
    public async Task Unmarked_scripts_open_and_are_upgraded_on_first_save()
    {
        await using var server = await TestServer.StartAsync("using System.Text;\nvar sb = new StringBuilder(\"hi\");\nConsole.WriteLine(sb);\n", "script.cs");
        var original = server.FileText();
        await using var client = await server.ConnectAsync();
        var code = client.CellIds().Single(id => id != "setup");
        await client.WaitFor(() => client.Stdout(code) == "hi\n");
        Assert.Equal(original, server.FileText());

        await client.Update(code, "var sb = new StringBuilder(\"hello\");\nConsole.WriteLine(sb);");
        await client.WaitFor(() => server.FileText().Contains("hello"));
        var saved = NotebookDocument.Parse(server.FileText());
        Assert.NotNull(saved.Preamble.Header);
        Assert.Contains($"// %% id={code}", server.FileText());
    }

    // ---- security ---------------------------------------------------------------------------

    [Fact]
    public async Task WebSocket_needs_the_token()
    {
        await using var server = await TestServer.StartAsync(Chain);
        var e = await Assert.ThrowsAsync<WebSocketException>(() => TestClient.ConnectAsync(server.Server, token: "wrong"));
        output.WriteLine(e.Message);
        await Assert.ThrowsAsync<WebSocketException>(() => TestClient.ConnectAsync(server.Server, token: ""));
    }

    [Fact]
    public async Task WebSocket_refuses_pages_from_other_origins()
    {
        await using var server = await TestServer.StartAsync(Chain);
        await Assert.ThrowsAsync<WebSocketException>(() => TestClient.ConnectAsync(server.Server, origin: "http://evil.example"));
        await Assert.ThrowsAsync<WebSocketException>(() => TestClient.ConnectAsync(server.Server, origin: $"http://127.0.0.1:{server.Server.Port + 1}"));
        await using var ok = await TestClient.ConnectAsync(server.Server, origin: $"http://127.0.0.1:{server.Server.Port}");
        Assert.True(ok.Snapshots > 0);
    }

    [Fact]
    public async Task Requests_for_other_host_names_are_refused()
    {
        await using var server = await TestServer.StartAsync(Chain);
        using var http = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, server.Server.BaseUrl);
        request.Headers.Host = "attacker.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(request)).StatusCode);
        var page = await http.GetAsync(server.Server.BaseUrl);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<div id=\"root\">", await page.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(server.Server.BaseUrl + "nope.js")).StatusCode);
    }

    [Fact]
    public void Server_listens_on_loopback_only()
    {
        Assert.Equal("127.0.0.1", new EditorOptions { NotebookPath = "x", KernelEntryAssembly = "y" }.Host);
    }

    static bool HasExited(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
