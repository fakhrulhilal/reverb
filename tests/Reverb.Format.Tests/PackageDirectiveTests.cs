using Reverb.Format;

namespace Reverb.Format.Tests;

public class PackageDirectiveTests
{
    const string Notebook = "#!/usr/bin/env dotnet\n#:package Reverb.Runtime@0.1.0\n#:package Humanizer@2.14.1\n#:property Nullable=enable\n// reverb-notebook v1\n\n// %% setup\nusing Reverb;\n";

    static NotebookDocument Parse(string text = Notebook) => NotebookDocument.Parse(text);

    [Fact]
    public void A_new_version_is_written_in_place()
    {
        var doc = Parse().WithPackages([new("Humanizer", "3.0.1")]);
        Assert.Equal(Notebook.Replace("Humanizer@2.14.1", "Humanizer@3.0.1"), doc.Serialize());
    }

    [Fact]
    public void A_new_package_goes_after_the_last_package_line()
    {
        var doc = Parse().WithPackages([new("Humanizer", "2.14.1"), new("Dapper", "2.1.66")]);
        Assert.Equal(Notebook.Replace("#:package Humanizer@2.14.1\n", "#:package Humanizer@2.14.1\n#:package Dapper@2.1.66\n"), doc.Serialize());
    }

    [Fact]
    public void A_removed_package_loses_its_line_and_the_runtime_stays()
    {
        var doc = Parse().WithPackages([]);
        Assert.Equal(Notebook.Replace("#:package Humanizer@2.14.1\n", ""), doc.Serialize());
    }

    [Fact]
    public void Names_match_without_regard_to_case()
    {
        var doc = Parse().WithPackages([new("humanizer", "3.0.1")]);
        Assert.Equal(Notebook.Replace("Humanizer@2.14.1", "humanizer@3.0.1"), doc.Serialize());
    }

    [Fact]
    public void The_runtime_package_is_not_changed_through_this()
    {
        var doc = Parse().WithPackages([new("Reverb.Runtime", "9.9.9"), new("Humanizer", "2.14.1")]);
        Assert.Equal(Notebook, doc.Serialize());
    }

    [Fact]
    public void Packages_are_added_after_the_runtime_when_there_are_no_others()
    {
        var text = Notebook.Replace("#:package Humanizer@2.14.1\n", "");
        var doc = Parse(text).WithPackages([new("Dapper", "2.1.66")]);
        Assert.Equal(text.Replace("#:package Reverb.Runtime@0.1.0\n", "#:package Reverb.Runtime@0.1.0\n#:package Dapper@2.1.66\n"), doc.Serialize());
    }

    [Fact]
    public void No_change_returns_the_same_document()
    {
        var doc = Parse();
        Assert.Same(doc, doc.WithPackages([new("Humanizer", "2.14.1")]));
    }

    [Theory]
    [InlineData("", "1.0.0")]
    [InlineData("Bad Name", "1.0.0")]
    [InlineData("Humanizer", "")]
    [InlineData("Humanizer", "1.0 beta")]
    [InlineData("Humanizer\n#:sdk Evil", "1.0.0")]
    public void Bad_names_and_versions_are_refused(string name, string version)
    {
        var e = Assert.Throws<ArgumentException>(() => Parse().WithPackages([new(name, version)]));
        Assert.Contains("package", e.Message, StringComparison.OrdinalIgnoreCase);
    }
}
