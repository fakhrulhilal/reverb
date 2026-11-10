namespace Reverb.Server.Tests;

/// <summary>Notebooks with NuGet packages in the editor. These restore Humanizer from nuget.org.</summary>
public class PackageTests
{
    const string Package = "#:package Humanizer@2.14.1\n";

    static string Notebook(bool withPackage) => $"""
        #:package Reverb.Runtime@0.1.0
        {(withPackage ? Package : "")}// reverb-notebook v1

        // %% setup
        using Reverb;

        // %% id=a
        Console.WriteLine(Humanizer.StringHumanizeExtensions.Humanize("some_text"));

        """;

    static bool SawState(TestClient client, string state) =>
        client.Messages.Any(m => m.Type == BrowserMessages.KernelStatus && m.State == state);

    [Fact]
    public async Task A_notebook_with_a_package_gets_a_kernel_that_has_it()
    {
        await using var server = await TestServer.StartAsync(Notebook(withPackage: true));
        await using var client = await server.ConnectAsync();
        await client.WaitFor(() => client.Stdout("a") == "some text\n", seconds: 180, what: "the cell to use the package");
        Assert.Equal([new PackageView { Name = "Humanizer", Version = "2.14.1" }], client.Packages, (x, y) => x.Name == y.Name && x.Version == y.Version);
    }

    [Fact]
    public async Task The_second_open_of_a_notebook_reuses_the_kernel_built_for_it()
    {
        var cache = TestServer.TempFolder();
        string Built() => Directory.GetDirectories(cache).Single();
        DateTime BuiltAt() => File.GetLastWriteTimeUtc(Path.Combine(Built(), "bin", "reverb-kernel.dll"));

        await using (var first = await TestServer.StartAsync(Notebook(withPackage: true), kernelCache: cache))
        {
            await using var client = await first.ConnectAsync();
            await client.WaitFor(() => client.Stdout("a") == "some text\n", seconds: 180, what: "the first open to run");
        }
        var dir = Built();
        var builtAt = BuiltAt();

        await using var second = await TestServer.StartAsync(Notebook(withPackage: true), kernelCache: cache);
        await using var again = await second.ConnectAsync();
        await again.WaitFor(() => again.Stdout("a") == "some text\n", seconds: 60, what: "the second open to run");
        Assert.Equal(dir, Built());
        Assert.Equal(builtAt, BuiltAt());
    }

    [Fact]
    public async Task Adding_and_removing_a_package_rebuilds_the_kernel_and_reruns()
    {
        await using var server = await TestServer.StartAsync(Notebook(withPackage: false));
        await using var client = await server.ConnectAsync();
        await client.WaitFor(() => client.Status("a") == "error", what: "the cell to fail without the package");

        await client.SendAsync(new ClientMessage { Type = BrowserMessages.PackagesSet, Packages = [new PackageView { Name = "Humanizer", Version = "2.14.1" }] });
        await client.WaitFor(() => client.Stdout("a") == "some text\n" && client.Status("a") == "idle", seconds: 180, what: "the cell to run with the package");
        Assert.True(SawState(client, "building"));
        Assert.Contains("#:package Humanizer@2.14.1\n", server.FileText());
        Assert.Single(client.Packages);

        await client.SendAsync(new ClientMessage { Type = BrowserMessages.PackagesSet, Packages = [] });
        await client.WaitFor(() => client.Status("a") == "error" && client.KernelState == "ready", seconds: 60, what: "the cell to fail again without the package");
        Assert.DoesNotContain("Humanizer@", server.FileText());
        Assert.Contains("#:package Reverb.Runtime@0.1.0", server.FileText());
        Assert.Empty(client.Packages);
    }

    [Fact]
    public async Task A_package_that_cannot_be_restored_says_why_and_removing_it_recovers()
    {
        var text = Notebook(withPackage: true).Replace("Humanizer@2.14.1", "Reverb.Tests.No.Such.Package@1.0.0");
        await using var server = await TestServer.StartAsync(text);
        await using var client = await server.ConnectAsync();
        await client.WaitFor(() => client.KernelState == "dead", seconds: 120, what: "the build to fail");
        var dead = client.Messages.Last(m => m.Type == BrowserMessages.KernelStatus && m.State == "dead");
        Assert.Contains("Reverb.Tests.No.Such.Package", dead.Message);
        Assert.Contains("NU1101", dead.Message);

        await client.SendAsync(new ClientMessage { Type = BrowserMessages.PackagesSet, Packages = [] });
        await client.WaitFor(() => client.KernelState == "ready" && client.Status("a") == "error", seconds: 60, what: "a plain kernel");
    }

    [Fact]
    public async Task A_bad_package_name_is_refused_with_a_reason()
    {
        await using var server = await TestServer.StartAsync(Notebook(withPackage: false));
        await using var client = await server.ConnectAsync();
        var before = server.FileText();
        await client.SendAsync(new ClientMessage { Type = BrowserMessages.PackagesSet, Packages = [new PackageView { Name = "Bad Name", Version = "1.0.0" }] });
        await client.WaitFor(() => client.Has(BrowserMessages.Error), what: "an error");
        Assert.Contains("isn't a valid package name", client.Messages.First(m => m.Type == BrowserMessages.Error).Message);
        await server.Host.IdleAsync();
        Assert.Equal(before, server.FileText());
    }

    [Fact]
    public async Task A_package_added_on_disk_rebuilds_the_kernel()
    {
        await using var server = await TestServer.StartAsync(Notebook(withPackage: false));
        await using var client = await server.ConnectAsync();
        await client.WaitFor(() => client.Status("a") == "error", what: "the cell to fail without the package");

        await File.WriteAllTextAsync(server.NotebookPath, Notebook(withPackage: true));
        await client.WaitFor(() => client.Stdout("a") == "some text\n", seconds: 180, what: "the cell to run with the package");
        Assert.Single(client.Packages);
    }

    [Fact]
    public async Task Edits_and_runs_made_while_the_kernel_builds_are_kept()
    {
        // A cache of its own, so the kernel really has to be built while we type.
        var cache = TestServer.TempFolder();
        await using var server = await TestServer.StartAsync(Notebook(withPackage: true), kernelCache: cache);
        await using var client = await server.ConnectAsync();
        Assert.Equal("building", client.KernelState);

        await client.Update("a", "Console.WriteLine(Humanizer.StringHumanizeExtensions.Humanize(\"edited_while_building\"));");
        await client.Run("a");
        await client.WaitFor(() => client.Stdout("a") == "edited while building\n", seconds: 180, what: "the edited cell to run once the kernel is up");
        Assert.Contains("edited_while_building", server.FileText());
    }
}
