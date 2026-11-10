using System.Text;
using Reverb.Testing;

namespace Reverb.Kernel.Tests;

/// <summary>The notebooks in samples/ stay clean and keep running the same with reverb exec and dotnet run.</summary>
[Collection("cli")]
public class SampleTests(ITestOutputHelper output)
{
    static string Samples => Path.Combine(Snapshot.RepoRoot, "samples");

    public static TheoryData<string> All => new(Directory.EnumerateFiles(Samples, "*.cs").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal));

    /// <summary>These call the network, so they're only checked, not run.</summary>
    static readonly HashSet<string> Online = ["github-explorer"];

    public static TheoryData<string> Offline => new(All.Select(d => (string)d.Data).Where(n => !Online.Contains(n)));

    [Theory]
    [MemberData(nameof(All))]
    public async Task Sample_has_no_problems(string name)
    {
        var r = await Cli.Reverb(Samples, "check", Path.Combine(Samples, name + ".cs"));
        output.WriteLine(r.Stdout + r.Stderr);
        Assert.Equal(0, r.ExitCode);
        Assert.EndsWith("no problems found.\n", r.Stdout.ReplaceLineEndings("\n"));
    }

    [Theory]
    [MemberData(nameof(Offline))]
    public async Task Sample_prints_what_dotnet_run_prints(string name)
    {
        var (dir, version) = await Cli.NotebookFolderAsync();
        var text = (await File.ReadAllTextAsync(Path.Combine(Samples, name + ".cs"))).Replace("#:package Reverb.Runtime@0.1.0", $"#:package Reverb.Runtime@{version}");
        var file = Path.Combine(dir, name + ".cs");
        await File.WriteAllTextAsync(file, text, new UTF8Encoding(false));
        CopyDirectory(Path.Combine(Samples, "data"), Path.Combine(dir, "data"));

        var build = await Cli.Dotnet(dir, "build", file);
        Assert.True(build.ExitCode == 0, build.Stdout + build.Stderr);
        var reference = await Cli.Dotnet(dir, "run", "--no-build", file);
        var exec = await Cli.Reverb(dir, "exec", file);
        output.WriteLine(exec.Stdout + exec.Stderr);
        Assert.Equal(0, reference.ExitCode);
        Assert.Equal(0, exec.ExitCode);
        Assert.NotEqual("", reference.Stdout.Trim());
        Assert.Equal(reference.Stdout.ReplaceLineEndings("\n"), exec.Stdout.ReplaceLineEndings("\n"));
    }

    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
    }
}
