using Reverb.Kernel;
using Reverb.Testing;

namespace Reverb.Server.Tests;

/// <summary>Completions, hover, signature help and code fixes through the server.</summary>
public class LanguageServerTests
{
    static async Task<ServerMessage> Ask(TestClient client, string type, string resultType, string cell, string source, int offset)
    {
        var id = Random.Shared.Next(1, int.MaxValue);
        await client.SendAsync(new ClientMessage { Type = type, RequestId = id, CellId = cell, Source = source, Offset = offset });
        await client.WaitFor(() => client.Messages.Any(m => m.Type == resultType && m.RequestId == id), what: resultType);
        return client.Messages.First(m => m.Type == resultType && m.RequestId == id);
    }

    [Fact]
    public async Task Completions_use_the_text_the_page_has_and_see_earlier_cells()
    {
        await using var server = await TestServer.StartAsync(NotebookText.Build(["var orders = new List<int> { 3 };", "var n = 1;"]));
        await using var client = await server.ConnectAsync();
        await client.WaitFor(() => client.Status("c2") == "idle");

        // The page hasn't sent this edit yet; the request carries it.
        var result = await Ask(client, BrowserMessages.LangCompletions, BrowserMessages.LangCompletionsResult, "c2", "var n = or", 10);
        Assert.Contains(result.Completions!, c => c.Label == "orders" && c.Kind == "variable");
        Assert.Equal(8, result.From);
        Assert.Equal(10, result.To);

        // Blank lines at the top of the cell aren't saved, but the page's offsets still line up.
        result = await Ask(client, BrowserMessages.LangCompletions, BrowserMessages.LangCompletionsResult, "c2", "\n\nvar n = or", 12);
        Assert.Contains(result.Completions!, c => c.Label == "orders");
        Assert.Equal(10, result.From);
        Assert.Equal(12, result.To);
    }

    [Fact]
    public async Task Hover_and_signature_help()
    {
        await using var server = await TestServer.StartAsync(NotebookText.Build(["var orders = new List<int> { 3 };", "var n = Math.Max(orders.Count, 1);"]));
        await using var client = await server.ConnectAsync();

        var hover = await Ask(client, BrowserMessages.LangHover, BrowserMessages.LangHoverResult, "c2", "var n = Math.Max(orders.Count, 1);", 19);
        Assert.Contains("Defined in cell 'c1'.", hover.Text);
        Assert.Equal(17, hover.From);

        var help = await Ask(client, BrowserMessages.LangSignatureHelp, BrowserMessages.LangSignatureHelpResult, "c2", "var n = Math.Max(orders.Count, 1);", 31);
        Assert.Equal(1, help.ActiveParameter);
        Assert.Contains(help.Signatures!, s => s.Label == "int Max(int val1, int val2)");
    }

    [Fact]
    public async Task A_misplaced_using_has_a_fix_that_moves_it_and_reruns()
    {
        var text = NotebookText.Build(["var a = 2;", "using System.Text;\nvar sb = new StringBuilder().Append(a);\nConsole.WriteLine(sb);"]);
        await using var server = await TestServer.StartAsync(text);
        await using var client = await server.ConnectAsync();
        await client.WaitFor(() => client["c2"].Diagnostics.Any(d => d.Id == "RVB004"), what: "the RVB004 diagnostic");
        var d = client["c2"].Diagnostics.First(d => d.Id == "RVB004");
        Assert.Equal("Move to the setup cell", d.Fix);

        await client.SendAsync(new ClientMessage { Type = BrowserMessages.LangCodeFix, CellId = "c2", DiagnosticId = d.Id, Line = d.StartLine, Column = d.StartColumn });
        await client.WaitFor(() => client.Stdout("c2") == "2\n", seconds: 60, what: "the fixed cell to run");
        Assert.Contains($"// %% setup\n{NotebookText.DefaultSetup}\nusing System.Text;\n", server.FileText());
        Assert.DoesNotContain("using System.Text;\nvar sb", server.FileText());
    }

    [Fact]
    public async Task An_anonymous_type_shared_between_cells_has_a_fix_that_makes_a_record()
    {
        var text = NotebookText.Build(["var q = new[] { 1, 2, 3 }.Select(x => new { X = x, Twice = x * 2 }).ToList();", "Console.WriteLine(q.Sum(r => r.Twice));"]);
        await using var server = await TestServer.StartAsync(text);
        await using var client = await server.ConnectAsync();
        await client.WaitFor(() => client["c2"].Diagnostics.Any(d => d.Id == "RVB002"), what: "the RVB002 diagnostic");
        var d = client["c2"].Diagnostics.First(d => d.Id == "RVB002");
        Assert.Equal("Use a record instead of the anonymous type", d.Fix);

        await client.SendAsync(new ClientMessage { Type = BrowserMessages.LangCodeFix, CellId = "c2", DiagnosticId = d.Id, Line = d.StartLine, Column = d.StartColumn });
        await client.WaitFor(() => client.Stdout("c2") == "12\n", seconds: 60, what: "the reading cell to run");
        Assert.Contains("record QRow(int X, int Twice);", server.FileText());
        Assert.Contains("new QRow(x, x * 2)", server.FileText());
    }

    [Fact]
    public async Task A_fix_for_a_problem_that_is_gone_says_so()
    {
        await using var server = await TestServer.StartAsync(NotebookText.Build(["var a = 1;"]));
        await using var client = await server.ConnectAsync();
        await client.SendAsync(new ClientMessage { Type = BrowserMessages.LangCodeFix, CellId = "c1", DiagnosticId = "RVB004", Line = 0, Column = 0 });
        await client.WaitFor(() => client.Has(BrowserMessages.Error), what: "an error");
        Assert.Contains("isn't there any more", client.Messages.First(m => m.Type == BrowserMessages.Error).Message);
    }

    [Fact]
    public async Task Apps_get_no_language_services()
    {
        await using var server = await TestServer.StartAsync(NotebookText.Build(["var a = 1;"]), appMode: true);
        await using var client = await server.ConnectAsync();
        await client.SendAsync(new ClientMessage { Type = BrowserMessages.LangCompletions, RequestId = 1, CellId = "c1", Source = "var a = 1;", Offset = 3 });
        await client.WaitFor(() => client.Has(BrowserMessages.Error), what: "the refusal");
        Assert.DoesNotContain(client.Messages, m => m.Type == BrowserMessages.LangCompletionsResult);
    }
}
