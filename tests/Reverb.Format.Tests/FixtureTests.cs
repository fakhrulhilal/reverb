using System.Text;
using Reverb.Format;
using Reverb.Testing;

namespace Reverb.Format.Tests;

public class FixtureTests
{
    public static TheoryData<string> Names => new(
        Directory.EnumerateFiles(Snapshot.FixturesDirectory, "*.cs")
            .Select(p => Path.GetFileNameWithoutExtension(p)!)
            .Order(StringComparer.Ordinal));

    static string PathOf(string name) => Path.Combine(Snapshot.FixturesDirectory, name + ".cs");

    [Theory]
    [MemberData(nameof(Names))]
    public void Round_trips_byte_for_byte(string name)
    {
        var path = PathOf(name);
        var original = File.ReadAllBytes(path);
        var temp = Path.Combine(Path.GetTempPath(), $"reverb-{Guid.NewGuid():N}.cs");
        try
        {
            NotebookFile.Save(temp, NotebookFile.Load(path));
            Assert.Equal(original, File.ReadAllBytes(temp));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Is_already_upgraded(string name)
    {
        var doc = NotebookFile.Load(PathOf(name));
        Assert.Empty(doc.Diagnostics);
        var (_, changes) = NotebookUpgrader.Upgrade(doc, new UpgradeOptions { RuntimeVersion = "0.1.0" });
        Assert.Empty(changes);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Structure(string name) =>
        Snapshot.Match(Describe(NotebookFile.Load(PathOf(name))), Path.Combine("tests", "fixtures", "snapshots", name + ".structure"));

    static string Describe(NotebookDocument doc)
    {
        var sb = new StringBuilder();
        sb.Append($"newline: {(doc.Newline == "\r\n" ? "crlf" : "lf")}, bom: {(doc.HasBom ? "yes" : "no")}\n");
        sb.Append("preamble:\n");
        if (doc.Preamble.Shebang is { } shebang) sb.Append($"  shebang: {shebang}\n");
        foreach (var d in doc.Preamble.Directives) sb.Append($"  directive: {d.Kind} {d.Name} {d.Value}\n");
        if (doc.Preamble.Header is { } h) sb.Append($"  header: v{h.Version}{string.Concat(h.Flags.Select(f => " " + f))}\n");
        sb.Append("cells:\n");
        for (var i = 0; i < doc.Cells.Length; i++)
        {
            var c = doc.Cells[i];
            sb.Append($"  [{i}] {c.Kind.ToString().ToLowerInvariant()}");
            if (c.Id is not null) sb.Append($" id={c.Id}");
            foreach (var a in c.Attributes) sb.Append(a.Value is null ? $" {a.Key}" : $" {a.Key}=\"{a.Value}\"");
            sb.Append('\n');
            foreach (var line in c.Source.Split('\n')) sb.Append("      | ").Append(line).Append('\n');
        }
        sb.Append("diagnostics:").Append(doc.Diagnostics.IsEmpty ? " none\n" : "\n");
        foreach (var d in doc.Diagnostics) sb.Append($"  line {d.Line} {d.Code}: {d.Message}\n");
        return sb.ToString();
    }
}
