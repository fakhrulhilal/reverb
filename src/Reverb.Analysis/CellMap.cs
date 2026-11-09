using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Reverb.Format;

namespace Reverb.Analysis;

/// <summary>Maps between positions in the notebook file and positions inside cells.</summary>
public sealed class CellMap
{
    readonly SerializedNotebook layout;
    readonly SourceText text;
    readonly int[] regionStarts; // file offset of each cell's marker line

    public CellMap(SerializedNotebook layout, SourceText text)
    {
        this.layout = layout;
        this.text = text;
        regionStarts = [.. layout.Cells.Select(c => text.Lines[c.MarkerLine].Start)];
    }

    public SerializedNotebook Layout => layout;

    /// <summary>The cell whose region (marker line up to the next marker) contains the position, or null for the preamble.</summary>
    public CellLayout? CellAt(int position)
    {
        var index = -1;
        for (var i = 0; i < regionStarts.Length && regionStarts[i] <= position; i++) index = i;
        return index < 0 ? null : layout.Cells[index];
    }

    public TextSpan RegionOf(CellLayout cell)
    {
        var start = regionStarts[cell.CellIndex];
        var end = cell.CellIndex + 1 < regionStarts.Length ? regionStarts[cell.CellIndex + 1] : text.Length;
        return TextSpan.FromBounds(start, end);
    }

    public CellRange ToCellRange(CellLayout cell, TextSpan span)
    {
        var lines = text.Lines.GetLinePositionSpan(span);
        var (sl, sc) = ToCell(cell, lines.Start);
        var (el, ec) = ToCell(cell, lines.End);
        return new CellRange(sl, sc, el, ec);
    }

    (int Line, int Column) ToCell(CellLayout cell, LinePosition p)
    {
        if (cell.ContentLineCount == 0) return (0, 0);
        var line = p.Line - cell.ContentStartLine;
        if (line < 0) return (0, 0);
        if (line >= cell.ContentLineCount)
        {
            var lastLine = cell.ContentLineCount - 1;
            return (lastLine, text.Lines[cell.ContentStartLine + lastLine].Span.Length);
        }
        return (line, p.Character);
    }

    /// <summary>File offset for a 0-based (line, column) inside a cell's source.</summary>
    public int ToFileOffset(CellLayout cell, int line, int column)
    {
        var fileLine = text.Lines[Math.Min(cell.ContentStartLine + line, text.Lines.Count - 1)];
        return fileLine.Start + Math.Min(column, fileLine.Span.Length);
    }

    /// <summary>File offset for an offset into a cell's <see cref="NotebookCell.Source"/> (which uses \n).</summary>
    public int ToFileOffset(CellLayout cell, string source, int offset)
    {
        var (line, column) = LineColumn(source, offset);
        return ToFileOffset(cell, line, column);
    }

    /// <summary>Offset into a cell's source for a file position inside the cell's content.</summary>
    public int ToSourceOffset(CellLayout cell, string source, int filePosition)
    {
        var (line, column) = ToCell(cell, text.Lines.GetLinePosition(filePosition));
        var starts = LineStarts(source);
        if (line >= starts.Count) return source.Length;
        return Math.Min(starts[line] + column, line + 1 < starts.Count ? starts[line + 1] - 1 : source.Length);
    }

    public static (int Line, int Column) LineColumn(string source, int offset)
    {
        var line = 0;
        var lineStart = 0;
        for (var i = 0; i < offset && i < source.Length; i++)
            if (source[i] == '\n') { line++; lineStart = i + 1; }
        return (line, offset - lineStart);
    }

    static List<int> LineStarts(string source)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < source.Length; i++) if (source[i] == '\n') starts.Add(i + 1);
        return starts;
    }
}
