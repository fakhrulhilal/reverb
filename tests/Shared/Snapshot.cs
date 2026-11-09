using System.Runtime.CompilerServices;

namespace Reverb.Testing;

/// <summary>
/// Minimal snapshot testing. Compares text with <c>&lt;name&gt;.verified.txt</c>. On a mismatch it
/// writes <c>&lt;name&gt;.received.txt</c> next to it and fails. Set REVERB_ACCEPT_SNAPSHOTS=1 to
/// accept the received text instead.
/// </summary>
public static class Snapshot
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string FixturesDirectory => Path.Combine(RepoRoot, "tests", "fixtures");

    public static void Match(string actual, string relativePath)
    {
        actual = actual.ReplaceLineEndings("\n");
        if (!actual.EndsWith('\n')) actual += "\n";

        var verified = Path.Combine(RepoRoot, relativePath + ".verified.txt");
        var received = Path.Combine(RepoRoot, relativePath + ".received.txt");
        var expected = File.Exists(verified) ? File.ReadAllText(verified).ReplaceLineEndings("\n") : null;

        if (expected == actual)
        {
            if (File.Exists(received)) File.Delete(received);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(verified)!);
        if (Environment.GetEnvironmentVariable("REVERB_ACCEPT_SNAPSHOTS") == "1")
        {
            File.WriteAllText(verified, actual);
            if (File.Exists(received)) File.Delete(received);
            return;
        }

        File.WriteAllText(received, actual);
        var message = expected is null
            ? $"No snapshot yet at {verified}. Check {received}, then rename it or rerun with REVERB_ACCEPT_SNAPSHOTS=1."
            : $"Snapshot differs from {verified}. First difference: {FirstDifference(expected, actual)}. Received text is in {received}.";
        Assert.Fail(message);
    }

    static string FirstDifference(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        for (var i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            var el = i < e.Length ? e[i] : "<end>";
            var al = i < a.Length ? a[i] : "<end>";
            if (el != al) return $"line {i + 1}: expected `{el}`, got `{al}`";
        }
        return "none";
    }

    static string FindRepoRoot([CallerFilePath] string here = "")
    {
        // Prefer walking up from the test binaries; fall back to this file's location at build time.
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(here)! })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Reverb.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("Couldn't find the repository root (Reverb.slnx).");
    }
}
