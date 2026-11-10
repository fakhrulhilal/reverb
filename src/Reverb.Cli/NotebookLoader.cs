using Reverb.Format;

namespace Reverb.Cli;

/// <summary>Loads a notebook for a command, upgrading it in memory (never on disk) if needed.</summary>
sealed record LoadedNotebook(string Path, NotebookDocument Original, NotebookDocument Document, IReadOnlyList<string> Upgrades)
{
    /// <summary>True when the file is already in Reverb's format, so analysed line numbers are file line numbers.</summary>
    public bool LinesMatchFile => Upgrades.Count == 0;

    public static LoadedNotebook? Load(string path, TextWriter errors)
    {
        if (!File.Exists(path))
        {
            errors.WriteLine($"error: '{path}' doesn't exist.");
            return null;
        }
        NotebookDocument doc;
        try
        {
            doc = NotebookFile.Load(path);
        }
        catch (InvalidDataException e)
        {
            errors.WriteLine($"error: {e.Message}");
            return null;
        }
        // Fixed seed: the ids given to unnamed cells are the same every time a file is loaded.
        var (upgraded, changes) = NotebookUpgrader.Upgrade(doc, new UpgradeOptions { RuntimeVersion = Program.RuntimeVersion, Random = new Random(0) });
        return new LoadedNotebook(path, doc, upgraded, changes);
    }
}

/// <summary>
/// MSBuild files above a notebook that <c>dotnet run</c> will pick up and Reverb doesn't.
/// They can make the two disagree, so commands warn about them.
/// </summary>
static class AmbientBuildFiles
{
    public sealed record Finding(string Path, string Why);

    public static IReadOnlyList<Finding> Find(string notebookPath)
    {
        var findings = new List<Finding>();
        var seen = new HashSet<string>();
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(notebookPath))!); dir is not null; dir = dir.Parent)
        {
            Check(dir, "Directory.Build.props", "dotnet run imports it, so its properties (for example TreatWarningsAsErrors) apply to the notebook.");
            Check(dir, "Directory.Build.targets", "dotnet run imports it and may change how the notebook builds.");
            Check(dir, "Directory.Packages.props", "if it turns on central package management, every '#:package Name@Version' line fails with NU1008. Add '#:property ManagePackageVersionsCentrally=false' to the notebook.");
            Check(dir, "global.json", "it can pin a different .NET SDK for dotnet run.");
        }
        return findings;

        void Check(DirectoryInfo dir, string name, string why)
        {
            // MSBuild uses the nearest file of each kind, so only the first one found matters.
            if (seen.Contains(name)) return;
            var path = Path.Combine(dir.FullName, name);
            if (!File.Exists(path)) return;
            seen.Add(name);
            if (IsEmptyProject(path)) return; // a stopper file: it sets nothing, it only stops MSBuild looking further up
            findings.Add(new Finding(path, why));
        }
    }

    /// <summary>A Project element with no elements in it (comments are fine) and no Sdk.</summary>
    static bool IsEmptyProject(string path)
    {
        if (!path.EndsWith(".props", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var root = System.Xml.Linq.XDocument.Load(path).Root;
            return root is { Name.LocalName: "Project" } && !root.Elements().Any() && root.Attribute("Sdk") is null;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }
}
