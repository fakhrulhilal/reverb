using Reverb.Testing;

namespace Reverb.Kernel.Tests;

[Collection("cli")]
public class ConvertCliTests(ITestOutputHelper output)
{
    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reverb-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Directory.Build.props"), "<Project />");
        return dir;
    }

    [Fact]
    public async Task A_converted_polyglot_notebook_checks_and_runs()
    {
        var dir = TempDir();
        var source = Path.Combine(dir, "orders.dib");
        File.Copy(Path.Combine(Snapshot.FixturesDirectory, "import", "orders.dib"), source);

        var r = await Cli.Reverb(dir, "convert", source);
        output.WriteLine(r.Stdout + r.Stderr);
        Assert.Equal(0, r.ExitCode);
        var converted = Path.Combine(dir, "orders.cs");
        Assert.Contains($"Wrote {converted}", r.Stdout);
        Assert.Contains("warning: " + source + ": cell 4: F# isn't supported", r.Stderr);

        var check = await Cli.Reverb(dir, "check", converted);
        output.WriteLine(check.Stdout);
        Assert.Equal(0, check.ExitCode);

        var exec = await Cli.Reverb(dir, "exec", converted);
        output.WriteLine(exec.Stdout + exec.Stderr);
        Assert.Equal(0, exec.ExitCode);
        Assert.Equal("2\ntotal sales\n", exec.Stdout.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Convert_refuses_to_overwrite_and_explains_bad_input()
    {
        var dir = TempDir();
        var source = Path.Combine(dir, "nb.ipynb");
        await File.WriteAllTextAsync(source, "{ broken");
        var r = await Cli.Reverb(dir, "convert", source);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("isn't valid JSON", r.Stderr);

        await File.WriteAllTextAsync(source, "{\"cells\": [], \"metadata\": {}, \"nbformat\": 4}");
        await File.WriteAllTextAsync(Path.Combine(dir, "nb.cs"), "// mine");
        r = await Cli.Reverb(dir, "convert", source);
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("already exists", r.Stderr);
        Assert.Equal("// mine", await File.ReadAllTextAsync(Path.Combine(dir, "nb.cs")));

        r = await Cli.Reverb(dir, "convert", source, "--force");
        Assert.Equal(0, r.ExitCode);

        r = await Cli.Reverb(dir, "convert", Path.Combine(dir, "nb.txt"));
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("isn't a .ipynb or .dib file", r.Stderr);
    }
}
