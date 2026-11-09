using Reverb.Format;

namespace Reverb.Format.Tests;

public class HeaderFlagTests
{
    const string Notebook = "#:package Reverb.Runtime@0.1.0\n// reverb-notebook v1\n\n// %% setup\nusing Reverb;\n\n// %% id=a\nvar a = 1;\n";

    [Fact]
    public void Turning_a_flag_on_appends_it_to_the_header()
    {
        var doc = NotebookDocument.Parse(Notebook).WithHeaderFlag("lazy", true);
        Assert.True(doc.Preamble.Header!.Lazy);
        Assert.Equal(Notebook.Replace("v1\n", "v1 lazy\n"), doc.Serialize());
    }

    [Fact]
    public void Turning_a_flag_off_removes_only_that_flag()
    {
        var doc = NotebookDocument.Parse(Notebook.Replace("v1\n", "v1 lazy wide\n")).WithHeaderFlag("lazy", false);
        Assert.Equal(["wide"], doc.Preamble.Header!.Flags);
        Assert.Equal(Notebook.Replace("v1\n", "v1 wide\n"), doc.Serialize());
    }

    [Fact]
    public void Setting_a_flag_that_is_already_set_changes_nothing()
    {
        var text = Notebook.Replace("v1\n", "v1 lazy\n");
        var doc = NotebookDocument.Parse(text);
        Assert.Same(doc, doc.WithHeaderFlag("lazy", true));
        Assert.Equal(text, doc.WithHeaderFlag("lazy", true).Serialize());
    }

    [Fact]
    public void A_notebook_without_a_header_gets_one()
    {
        var doc = NotebookDocument.Parse("#:package Reverb.Runtime@0.1.0\n\n// %% setup\nusing Reverb;\n").WithHeaderFlag("lazy", true);
        Assert.Equal("#:package Reverb.Runtime@0.1.0\n// reverb-notebook v1 lazy\n\n// %% setup\nusing Reverb;\n", doc.Serialize());
    }

    [Fact]
    public void Crlf_files_keep_crlf()
    {
        var text = Notebook.ReplaceLineEndings("\r\n");
        var doc = NotebookDocument.Parse(text).WithHeaderFlag("lazy", true);
        Assert.Equal(text.Replace("v1\r\n", "v1 lazy\r\n"), doc.Serialize());
    }

    [Fact]
    public void Flags_must_be_single_words()
    {
        var doc = NotebookDocument.Parse(Notebook);
        Assert.Throws<ArgumentException>(() => doc.WithHeaderFlag("two words", true));
    }
}
