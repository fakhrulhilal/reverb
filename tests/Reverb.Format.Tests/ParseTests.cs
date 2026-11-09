using Reverb.Format;

namespace Reverb.Format.Tests;

public class ParseTests
{
    const string Sales = """
        #!/usr/bin/env dotnet
        #:package Reverb.Runtime@0.1.0
        // reverb-notebook v1

        // %% setup
        using Reverb;

        // %% [markdown]
        // # Sales explorer
        // Drag the slider to change the minimum order size.

        // %% id=data
        var rng = new Random(42);
        string[] regions = ["West", "East", "Central"];

        // %% id=threshold name="Pick a threshold" hide_code
        var threshold = Ui.Slider("Minimum order", min: 0, max: 1000, value: 400);

        // %% types
        record Order(int Id, string Region, double Total);

        """;

    [Fact]
    public void Parses_cells_in_order()
    {
        var doc = NotebookDocument.Parse(Sales);
        Assert.Empty(doc.Diagnostics);
        Assert.Equal(
            [CellKind.Setup, CellKind.Markdown, CellKind.Code, CellKind.Code, CellKind.Types],
            doc.Cells.Select(c => c.Kind));
        Assert.Equal(["setup", null, "data", "threshold", "types"], doc.Cells.Select(c => c.Key));
    }

    [Fact]
    public void Source_excludes_marker_and_surrounding_blank_lines()
    {
        var doc = NotebookDocument.Parse(Sales);
        Assert.Equal("using Reverb;", doc.Cells[0].Source);
        Assert.Equal("var rng = new Random(42);\nstring[] regions = [\"West\", \"East\", \"Central\"];", doc.Cells[2].Source);
        Assert.Equal("record Order(int Id, string Region, double Total);", doc.Cells[4].Source);
    }

    [Fact]
    public void Markdown_source_has_comment_prefix_stripped()
    {
        var doc = NotebookDocument.Parse("// %% [markdown]\n// # Title\n//\n//no space\n//   indented\n");
        Assert.Equal("# Title\n\nno space\n  indented", doc.Cells[0].Source);
    }

    [Fact]
    public void Attributes_are_parsed()
    {
        var cell = NotebookDocument.Parse(Sales).Cells[3];
        Assert.Equal("threshold", cell.Id);
        Assert.Equal("Pick a threshold", cell.Name);
        Assert.True(cell.HideCode);
        Assert.False(cell.Disabled);
    }

    [Fact]
    public void Quoted_attribute_values_support_escapes()
    {
        var cell = NotebookDocument.Parse("// %% id=a name=\"say \\\"hi\\\" \\\\ bye\" disabled\nvar x = 1;\n").Cells[0];
        Assert.Equal("say \"hi\" \\ bye", cell.Name);
        Assert.True(cell.Disabled);
    }

    [Fact]
    public void Preamble_exposes_shebang_directives_and_header()
    {
        var doc = NotebookDocument.Parse("#!/usr/bin/env dotnet\n#:package Humanizer@2.14.1\n#:property LangVersion=preview\n#:sdk Microsoft.NET.Sdk.Web\n// reverb-notebook v1 lazy\n\n// %% setup\n");
        Assert.Equal("#!/usr/bin/env dotnet", doc.Preamble.Shebang);
        Assert.Equal(
            [new Directive("package", "Humanizer", "2.14.1"), new Directive("property", "LangVersion", "preview"), new Directive("sdk", "Microsoft.NET.Sdk.Web", null)],
            doc.Preamble.Directives);
        Assert.Equal(1, doc.Preamble.Header!.Version);
        Assert.True(doc.Preamble.Header.Lazy);
    }

    [Fact]
    public void A_marker_needs_column_zero_and_a_space_after()
    {
        var doc = NotebookDocument.Parse("// %% id=a\nvar x = 1;\n  // %% id=b\n// %%%\n//%% id=c\nvar y = 2;\n");
        Assert.Single(doc.Cells);
        Assert.Equal("var x = 1;\n  // %% id=b\n// %%%\n//%% id=c\nvar y = 2;", doc.Cells[0].Source);
    }

    [Fact]
    public void Bare_marker_is_a_code_cell_without_id()
    {
        var doc = NotebookDocument.Parse("// %%\nvar x = 1;\n");
        Assert.Equal(CellKind.Code, doc.Cells[0].Kind);
        Assert.Null(doc.Cells[0].Id);
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void Jupytext_style_titles_are_kept_as_attributes()
    {
        var cell = NotebookDocument.Parse("// %% Load data\nvar x = 1;\n").Cells[0];
        Assert.Equal(["Load", "data"], cell.Attributes.Select(a => a.Key));
    }

    [Fact]
    public void Duplicate_ids_are_reported_and_cleared()
    {
        var doc = NotebookDocument.Parse("// %% id=a\nvar x = 1;\n\n// %% id=a\nvar y = 2;\n");
        Assert.Equal("a", doc.Cells[0].Id);
        Assert.Null(doc.Cells[1].Id);
        var d = Assert.Single(doc.Diagnostics);
        Assert.Equal("RVB101", d.Code);
        Assert.Equal(4, d.Line);
    }

    [Theory]
    [InlineData("Upper")]
    [InlineData("has space\"")]
    [InlineData("setup")]
    [InlineData("types")]
    [InlineData("abcdefghijabcdefghijabcdefghijabc")]
    public void Invalid_ids_are_reported_and_cleared(string id)
    {
        var doc = NotebookDocument.Parse($"// %% id=\"{id.Replace("\"", "\\\"")}\"\nvar x = 1;\n");
        Assert.Null(doc.Cells[0].Id);
        Assert.Equal("RVB102", Assert.Single(doc.Diagnostics).Code);
    }

    [Fact]
    public void Unterminated_quote_is_reported()
    {
        var doc = NotebookDocument.Parse("// %% id=a name=\"oops\nvar x = 1;\n");
        Assert.Equal("RVB103", Assert.Single(doc.Diagnostics).Code);
        Assert.Equal("a", doc.Cells[0].Id);
    }

    [Fact]
    public void Code_inside_markdown_cell_becomes_its_own_code_cell()
    {
        var doc = NotebookDocument.Parse("// %% [markdown]\n// # Title\n\nvar x = 1;\n");
        Assert.Equal([CellKind.Markdown, CellKind.Code], doc.Cells.Select(c => c.Kind));
        Assert.Equal("# Title", doc.Cells[0].Source);
        Assert.Equal("var x = 1;", doc.Cells[1].Source);
        Assert.Equal("RVB104", Assert.Single(doc.Diagnostics).Code);
    }

    [Fact]
    public void Setup_after_code_and_types_before_code_are_reported()
    {
        var doc = NotebookDocument.Parse("// %% id=a\nvar x = 1;\n// %% setup\nusing System;\n// %% types\nrecord R;\n// %% id=b\nvar y = 2;\n");
        Assert.Equal(["RVB105", "RVB105"], doc.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void Line_endings_are_detected()
    {
        Assert.Equal("\r\n", NotebookDocument.Parse("// %% id=a\r\nvar x = 1;\r\n").Newline);
        Assert.Equal("\n", NotebookDocument.Parse("// %% id=a\nvar x = 1;\n").Newline);
        Assert.Equal("\n", NotebookDocument.Parse("").Newline);
    }

    [Fact]
    public void Crlf_source_is_normalised_to_lf()
    {
        var doc = NotebookDocument.Parse("// %% id=a\r\nvar x = 1;\r\nvar y = 2;\r\n");
        Assert.Equal("var x = 1;\nvar y = 2;", doc.Cells[0].Source);
    }

    [Fact]
    public void Bom_is_detected()
    {
        var doc = NotebookDocument.Parse("﻿// %% id=a\nvar x = 1;\n");
        Assert.True(doc.HasBom);
        Assert.Equal("a", doc.Cells[0].Id);
    }
}
