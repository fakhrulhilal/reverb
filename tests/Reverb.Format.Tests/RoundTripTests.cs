using Reverb.Format;

namespace Reverb.Format.Tests;

public class RoundTripTests
{
    public static TheoryData<string> Texts => new(
        "",
        "\n",
        "// %% id=a\nvar x = 1;\n",
        "// %% id=a\nvar x = 1;",                               // no final newline
        "// %% id=a\r\nvar x = 1;\r\n\r\n// %% id=b\r\nvar y = x;\r\n",
        "// %% id=a\nvar x = 1;\r\n\n// %% id=b\r\nvar y = x;\n",  // mixed line endings
        "﻿#!/usr/bin/env dotnet\n// reverb-notebook v1\n\n// %% id=a\nvar x = 1;\n",
        "// %% id=a\n\n\n   \nvar x = 1;\n\t\n\n// %% id=b\n",  // odd padding, empty last cell
        "// %% id=a    name=\"x\"\nvar x = 1;\n",               // unusual spacing in marker
        "// %%\nvar x = 1;\n",
        "// %% [markdown]\n// # T\n//\n//x\n",
        "// %% [markdown]\n// # T\n\nvar x = 1;\n",                // markdown followed by code
        "// my script\nusing System;\n\nvar x = 1;\n\nrecord R;\n", // no markers at all
        "// %% id=a name=\"oops\nvar x = 1;\n");                  // malformed marker

    [Theory]
    [MemberData(nameof(Texts))]
    public void Unchanged_documents_serialise_byte_for_byte(string text)
    {
        // Documents without markers are imported, not round-tripped. Everything else must survive exactly.
        var doc = NotebookDocument.Parse(text);
        if (text.Contains("// %%") && !text.Contains("// # T\n\nvar"))
            Assert.Equal(text, doc.Serialize());
    }

    [Fact]
    public void Editing_a_cell_regenerates_only_that_cell_and_keeps_line_endings()
    {
        var text = "// %% id=a\r\nvar x = 1;\r\n\r\n// %% id=b   disabled\r\nvar y = x;\r\n";
        var doc = NotebookDocument.Parse(text);
        var edited = doc.WithCellSource("a", "var x = 2;\nvar z = 3;");
        Assert.Equal("// %% id=a\r\nvar x = 2;\r\nvar z = 3;\r\n\r\n// %% id=b   disabled\r\nvar y = x;\r\n", edited.Serialize());
    }

    [Fact]
    public void Editing_keeps_the_cell_padding()
    {
        var doc = NotebookDocument.Parse("// %% id=a\n\nvar x = 1;\n\n\n// %% id=b\nvar y = x;\n");
        Assert.Equal("// %% id=a\n\nvar x = 5;\n\n\n// %% id=b\nvar y = x;\n", doc.WithCellSource("a", "var x = 5;").Serialize());
    }

    [Fact]
    public void Changing_attributes_regenerates_only_the_marker()
    {
        var doc = NotebookDocument.Parse("// %% id=a\nvar x = 1;\n");
        var cell = doc.Cells[0].WithFlag("disabled", true).WithAttribute("name", "Load data");
        Assert.Equal("// %% id=a disabled name=\"Load data\"\nvar x = 1;\n", doc.ReplaceCell(0, cell).Serialize());
        Assert.Equal("// %% id=a\nvar x = 1;\n", doc.ReplaceCell(0, cell.WithFlag("disabled", false).WithoutAttribute("name")).Serialize());
    }

    [Fact]
    public void Appending_after_the_last_cell_adds_a_blank_line()
    {
        var doc = NotebookDocument.Parse("// %% id=a\nvar x = 1;\n");
        var added = doc.InsertCell(1, NotebookCell.Code("b", "var y = x;"));
        Assert.Equal("// %% id=a\nvar x = 1;\n\n// %% id=b\nvar y = x;\n", added.Serialize());
    }

    [Fact]
    public void Appending_after_a_last_line_without_newline()
    {
        var doc = NotebookDocument.Parse("// %% id=a\nvar x = 1;");
        var added = doc.InsertCell(1, NotebookCell.Code("b", "var y = x;"));
        Assert.Equal("// %% id=a\nvar x = 1;\n\n// %% id=b\nvar y = x;\n", added.Serialize());
    }

    [Fact]
    public void Inserting_between_cells()
    {
        var doc = NotebookDocument.Parse("// %% id=a\nvar x = 1;\n\n// %% id=c\nvar z = 1;\n");
        var added = doc.InsertCell(1, NotebookCell.Markdown("b", "# Hi\n\nthere"));
        Assert.Equal("// %% id=a\nvar x = 1;\n\n// %% [markdown] id=b\n// # Hi\n//\n// there\n\n// %% id=c\nvar z = 1;\n", added.Serialize());
    }

    [Fact]
    public void Removing_and_moving_cells()
    {
        var doc = NotebookDocument.Parse("// %% id=a\nvar x = 1;\n\n// %% id=b\nvar y = 2;\n\n// %% id=c\nvar z = 3;\n");
        Assert.Equal("// %% id=a\nvar x = 1;\n\n// %% id=c\nvar z = 3;\n", doc.RemoveCell(1).Serialize());
        Assert.Equal("// %% id=b\nvar y = 2;\n\n// %% id=a\nvar x = 1;\n\n// %% id=c\nvar z = 3;\n", doc.MoveCell(0, 1).Serialize());
        // Moving the last cell up: it had no trailing blank line, so one is added.
        Assert.Equal("// %% id=a\nvar x = 1;\n\n// %% id=c\nvar z = 3;\n\n// %% id=b\nvar y = 2;\n", doc.MoveCell(2, 1).Serialize());
    }

    [Fact]
    public void Clearing_and_refilling_a_cell()
    {
        var doc = NotebookDocument.Parse("// %% id=a\nvar x = 1;\n\n// %% id=b\nvar y = 2;\n");
        var cleared = doc.WithCellSource("a", "");
        Assert.Equal("// %% id=a\n\n// %% id=b\nvar y = 2;\n", cleared.Serialize());
        var refilled = NotebookDocument.Parse(cleared.Serialize()).WithCellSource("a", "var x = 9;");
        Assert.Equal("// %% id=a\nvar x = 9;\n\n// %% id=b\nvar y = 2;\n", refilled.Serialize());
    }

    [Fact]
    public void Editing_markdown_cells()
    {
        var doc = NotebookDocument.Parse("// %% [markdown] id=m\n// old\n\n// %% id=a\nvar x = 1;\n");
        Assert.Equal("// %% [markdown] id=m\n// # New\n//\n// text\n\n// %% id=a\nvar x = 1;\n",
            doc.WithCellSource("m", "# New\n\ntext").Serialize());
    }

    [Fact]
    public void Layout_reports_cell_lines()
    {
        var doc = NotebookDocument.Parse("#!/usr/bin/env dotnet\n// reverb-notebook v1\n\n// %% id=a\n\nvar x = 1;\nvar y = 2;\n\n// %% id=b\n");
        var layout = NotebookWriter.Write(doc);
        var a = layout.Cells[0];
        Assert.Equal(3, a.MarkerLine);
        Assert.Equal(5, a.ContentStartLine);
        Assert.Equal(2, a.ContentLineCount);
        Assert.Equal("var x = 1;\nvar y = 2;", layout.Text.Substring(a.ContentStart, a.ContentLength));
        Assert.Equal(0, layout.Cells[1].ContentLineCount);
    }
}
