using System.Collections.Concurrent;
using System.Text.Json;
using Reverb.Testing;

namespace Reverb.Kernel.Tests;

[CollectionDefinition("cli", DisableParallelization = true)]
public class CliCollection;

[Collection("cli")]
public class CliTests(ITestOutputHelper output)
{
    static IEnumerable<string> CleanFixtures() =>
        Directory.EnumerateFiles(Snapshot.FixturesDirectory, "*.cs")
            .Select(p => Path.GetFileNameWithoutExtension(p)!)
            .Where(n => !n.StartsWith("diag-", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    static string Normalize(string s) => s.ReplaceLineEndings("\n");

    [Fact]
    public async Task Exec_prints_what_dotnet_run_prints_for_every_fixture()
    {
        var (dir, version) = await Cli.NotebookFolderAsync();
        var mismatches = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(CleanFixtures(), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (name, _) =>
        {
            var file = await Cli.CopyFixtureAsync(name, dir, version);
            var build = await Cli.Dotnet(dir, "build", file);
            if (build.ExitCode != 0)
            {
                mismatches.Add($"{name}: dotnet build failed\n{build.Stdout}{build.Stderr}");
                return;
            }
            var reference = await Cli.Dotnet(dir, "run", "--no-build", file, "--", "one", "two");
            var exec = await Cli.Reverb(dir, "exec", file, "--", "one", "two");
            if (exec.ExitCode != 0 || reference.ExitCode != 0)
                mismatches.Add($"{name}: exit codes reverb={exec.ExitCode} dotnet={reference.ExitCode}\n{exec.Stderr}\n{reference.Stderr}");
            else if (reference.Stdout.Trim().Length == 0)
                mismatches.Add($"{name}: dotnet run printed nothing, so the comparison proves nothing");
            else if (Normalize(exec.Stdout) != Normalize(reference.Stdout))
                mismatches.Add($"{name}: output differs\n--- reverb exec ---\n{exec.Stdout}--- dotnet run ---\n{reference.Stdout}");
        });
        output.WriteLine($"compared {CleanFixtures().Count()} fixtures");
        Assert.True(CleanFixtures().Count() >= 20);
        Assert.True(mismatches.IsEmpty, string.Join("\n\n", mismatches));
    }

    [Fact]
    public async Task Exec_fails_when_a_cell_fails_but_still_runs_the_others()
    {
        var (dir, version) = await Cli.NotebookFolderAsync();
        var file = await Cli.CopyFixtureAsync("diag-errors", dir, version);
        var r = await Cli.Reverb(dir, "exec", file);
        output.WriteLine(r.Stdout + r.Stderr);
        Assert.Equal(1, r.ExitCode);
        Assert.Equal("this cell is fine\n", Normalize(r.Stdout));
        Assert.Contains("error in cell 'reassign', line 1: RVB001:", r.Stderr);
        Assert.Contains("error in cell 'uses-anon', line 1: RVB002:", r.Stderr);
    }

    [Fact]
    public async Task Exec_reports_runtime_errors_with_the_cell_and_line()
    {
        var (dir, _) = await Cli.NotebookFolderAsync();
        var file = Path.Combine(dir, "boom.cs");
        await File.WriteAllTextAsync(file, NotebookText.Build(["var zero = 0;", "Console.WriteLine(\"before\");\nvar x = 1 / zero;", "Console.WriteLine(\"independent\");"]));
        var r = await Cli.Reverb(dir, "exec", file);
        Assert.Equal(1, r.ExitCode);
        Assert.Equal("before\nindependent\n", Normalize(r.Stdout));
        Assert.Contains("error in cell 'c2': DivideByZeroException: Attempted to divide by zero.\n  at cell 'c2', line 2", Normalize(r.Stderr));
    }

    [Fact]
    public async Task Exec_json_describes_every_cell()
    {
        var (dir, version) = await Cli.NotebookFolderAsync();
        var file = await Cli.CopyFixtureAsync("chain", dir, version);
        var r = await Cli.Reverb(dir, "exec", file, "--json");
        Assert.Equal(0, r.ExitCode);
        using var json = JsonDocument.Parse(r.Stdout);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        var cells = json.RootElement.GetProperty("cells").EnumerateArray().ToList();
        Assert.Equal(["a", "b", "c", "d"], cells.Select(c => c.GetProperty("id").GetString()));
        Assert.All(cells, c => Assert.Equal("idle", c.GetProperty("status").GetString()));
        var d = cells[3].GetProperty("outputs")[0];
        Assert.Equal("stdout", d.GetProperty("stream").GetString());
        Assert.Equal("a=2 b=20 c=21\n", Normalize(d.GetProperty("text").GetString()!));
    }

    [Fact]
    public async Task Exec_runs_with_the_same_runtime_switches_as_dotnet_run()
    {
        var (dir, version) = await Cli.NotebookFolderAsync();
        var file = Path.Combine(dir, "switches.cs");
        // Every switch Reverb copies, as the running program sees it. If an SDK changes one, this fails.
        var probes = FileBasedAppRuntime.AotSwitches.Keys
            .Select((name, i) => $"Console.WriteLine(\"{name}=\" + (AppContext.TryGetSwitch(\"{name}\", out var s{i}) ? s{i}.ToString() : \"unset\"));");
        await File.WriteAllTextAsync(file, NotebookText.Build([
            "Console.WriteLine(System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported);",
            "Console.WriteLine(System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault);",
            "Console.WriteLine(Path.GetFileName((string?)AppContext.GetData(\"EntryPointFilePath\")));",
            string.Join("\n", probes),
        ]).Replace("Reverb.Runtime@0.1.0", $"Reverb.Runtime@{version}"));
        Assert.Equal(0, (await Cli.Dotnet(dir, "build", file)).ExitCode);
        var reference = await Cli.Dotnet(dir, "run", "--no-build", file);
        var exec = await Cli.Reverb(dir, "exec", file);
        output.WriteLine(reference.Stdout);
        Assert.StartsWith("False\nFalse\nswitches.cs\n", Normalize(reference.Stdout));
        Assert.DoesNotContain("=unset", reference.Stdout);
        Assert.Equal(Normalize(reference.Stdout), Normalize(exec.Stdout));
    }

    [Fact]
    public async Task Check_passes_a_clean_notebook()
    {
        var r = await Cli.Reverb(Snapshot.RepoRoot, "check", Path.Combine(Snapshot.FixturesDirectory, "sales.cs"));
        output.WriteLine(r.Stdout);
        Assert.Equal(0, r.ExitCode);
        Assert.EndsWith("no problems found.\n", Normalize(r.Stdout));
    }

    [Fact]
    public async Task Check_reports_errors_at_file_positions()
    {
        var path = Path.Combine(Snapshot.FixturesDirectory, "diag-errors.cs");
        var r = await Cli.Reverb(Snapshot.RepoRoot, "check", path);
        output.WriteLine(r.Stdout);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains($"{path}(13,1) [cell reassign]: error RVB001:", r.Stdout);
        Assert.Contains($"{path}(10,5) [cell source]: warning RVB002:", r.Stdout);
        Assert.Contains("2 errors, 2 warnings.", r.Stdout);
    }

    [Fact]
    public async Task Check_handles_a_plain_script_and_warns_about_parent_build_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reverb-tests", Guid.NewGuid().ToString("N")[..12]);
        var inner = Path.Combine(dir, "inner");
        Directory.CreateDirectory(inner);
        await File.WriteAllTextAsync(Path.Combine(dir, "Directory.Build.props"), "<Project><PropertyGroup><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>");
        var file = Path.Combine(inner, "script.cs");
        await File.WriteAllTextAsync(file, "using System.Text;\nvar sb = new StringBuilder();\nsb.Append(undefined);\n");
        var r = await Cli.Reverb(inner, "check", file);
        output.WriteLine(r.Stdout);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("isn't in Reverb's notebook format yet", r.Stdout);
        Assert.Contains("warning RVB200: dotnet run imports it", r.Stdout);
        Assert.Contains("[cell ", r.Stdout);
        Assert.Contains("error CS0103: The name 'undefined' does not exist", r.Stdout);
    }

    [Fact]
    public async Task Unknown_command_and_version()
    {
        Assert.Equal(2, (await Cli.Reverb(Snapshot.RepoRoot, "frobnicate")).ExitCode);
        var v = await Cli.Reverb(Snapshot.RepoRoot, "--version");
        Assert.Equal("0.1.0\n", Normalize(v.Stdout));
    }
}
