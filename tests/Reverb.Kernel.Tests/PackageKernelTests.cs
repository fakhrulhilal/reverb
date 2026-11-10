using System.Collections.Concurrent;
using Reverb.Format;
using Reverb.Testing;

namespace Reverb.Kernel.Tests;

/// <summary>Kernels built for a notebook's NuGet packages. These restore real packages from nuget.org.</summary>
[Collection("cli")]
public class PackageKernelTests
{
    const string Humanizer = "#:package Humanizer@2.14.1";

    static string Notebook(string runtimeVersion = "0.1.0") => $"""
        #!/usr/bin/env dotnet
        #:package Reverb.Runtime@{runtimeVersion}
        {Humanizer}
        // reverb-notebook v1

        // %% setup
        using Reverb;
        using Humanizer;

        // %% id=a
        var text = "some_text".Humanize();
        Console.WriteLine(text);

        """;

    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reverb-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Directory.Build.props"), "<Project />");
        return dir;
    }

    static KernelBuildRequest Request(string cache, string text, string? notebookDirectory = null) => new()
    {
        Packages = KernelBuilder.PackagesOf(NotebookDocument.Parse(text).Preamble.Directives),
        ToolDirectory = Path.GetDirectoryName(Cli.CliAssembly)!,
        NotebookDirectory = notebookDirectory,
        CacheRoot = cache,
    };

    [Fact]
    public void Packages_leave_out_the_runtime_and_keep_versions()
    {
        var doc = NotebookDocument.Parse("#:package Reverb.Runtime@0.1.0\n#:package Humanizer@2.14.1\n#:package Dapper@2.1.66\n// reverb-notebook v1\n");
        Assert.Equal([new NotebookPackage("Dapper", "2.1.66"), new NotebookPackage("Humanizer", "2.14.1")], KernelBuilder.PackagesOf(doc.Preamble.Directives));
    }

    [Fact]
    public async Task No_packages_means_the_plain_kernel()
    {
        Assert.Null(await KernelBuilder.EnsureAsync(Request(TempDir(), "#:package Reverb.Runtime@0.1.0\n// reverb-notebook v1\n")));
    }

    [Fact]
    public async Task Builds_a_kernel_with_a_package_once_and_reuses_it()
    {
        var cache = TempDir();
        var steps = new List<string>();
        var build = await KernelBuilder.EnsureAsync(Request(cache, Notebook()), steps.Add);
        Assert.NotNull(build);
        Assert.False(build.Reused);
        Assert.Contains(steps, s => s.Contains("Humanizer@2.14.1"));
        Assert.Contains(build.References, r => Path.GetFileName(r) == "Humanizer.dll");

        var client = await KernelClient.StartAsync(new KernelLaunch { EntryAssembly = Cli.CliAssembly }.WithBuild(build));
        await using (client)
        {
            var stdout = new ConcurrentQueue<string>();
            client.Event += e => { if (e.Type == EventTypes.Stream && e.Cell == "a") stdout.Enqueue(e.Text!); };
            var done = await client.SendAsync(new KernelRequest { Type = RequestTypes.Open, Text = Notebook(), Run = true });
            Assert.True(done.Ok);
            Assert.Equal("some text\n", string.Concat(stdout).ReplaceLineEndings("\n"));
        }

        steps.Clear();
        var again = await KernelBuilder.EnsureAsync(Request(cache, Notebook()), steps.Add);
        Assert.NotNull(again);
        Assert.True(again.Reused);
        Assert.Equal(build.Directory, again.Directory);
        Assert.Empty(steps);
    }

    [Fact]
    public async Task Two_builds_of_the_same_packages_at_once_build_once()
    {
        var cache = TempDir();
        var builds = await Task.WhenAll(KernelBuilder.EnsureAsync(Request(cache, Notebook())), KernelBuilder.EnsureAsync(Request(cache, Notebook())));
        Assert.Single(builds, b => !b!.Reused);
        Assert.Equal(builds[0]!.Directory, builds[1]!.Directory);
    }

    [Fact]
    public async Task A_package_that_does_not_exist_is_named_in_the_error()
    {
        var text = Notebook().Replace(Humanizer, "#:package Reverb.Tests.No.Such.Package@1.0.0");
        var e = await Assert.ThrowsAsync<KernelBuildException>(() => KernelBuilder.EnsureAsync(Request(TempDir(), text)));
        Assert.Contains("NU1101", e.Message);
        Assert.Contains("Reverb.Tests.No.Such.Package", e.Message);
        Assert.Contains("#:package", e.Message);
    }

    [Fact]
    public async Task A_package_without_a_version_says_how_to_add_one()
    {
        var text = Notebook().Replace(Humanizer, "#:package Humanizer");
        var e = await Assert.ThrowsAsync<KernelBuildException>(() => KernelBuilder.EnsureAsync(Request(TempDir(), text)));
        Assert.Contains("'#:package Humanizer@", e.Message);
    }

    [Fact]
    public async Task Exec_and_check_with_a_package_agree_with_dotnet_run()
    {
        var (dir, runtimeVersion) = await Cli.NotebookFolderAsync();
        var path = Path.Combine(dir, "humanize.cs");
        await File.WriteAllTextAsync(path, Notebook(runtimeVersion));

        var built = await Cli.Dotnet(dir, "build", "humanize.cs");
        Assert.True(built.ExitCode == 0, built.Stdout + built.Stderr);
        var expected = await Cli.Dotnet(dir, "run", "--no-build", "humanize.cs");
        Assert.True(expected.ExitCode == 0, expected.Stdout + expected.Stderr);

        var exec = await Cli.Reverb(dir, "exec", "humanize.cs");
        Assert.True(exec.ExitCode == 0, exec.Stdout + exec.Stderr);
        Assert.Equal(expected.Stdout.ReplaceLineEndings("\n"), exec.Stdout.ReplaceLineEndings("\n"));

        var check = await Cli.Reverb(dir, "check", "humanize.cs");
        Assert.True(check.ExitCode == 0, check.Stdout + check.Stderr);
        Assert.Contains("no problems found", check.Stdout);
    }
}
