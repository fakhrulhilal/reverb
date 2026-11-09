using System.Collections.Immutable;
using System.Text;

namespace Reverb.Format;

/// <summary>Where a cell ended up in the written text. Lines are 0-based; offsets exclude any BOM.</summary>
public sealed record CellLayout(
    int CellIndex,
    string? Key,
    CellKind Kind,
    int MarkerLine,
    int ContentStartLine,
    int ContentLineCount,
    int ContentStart,
    int ContentLength);

public sealed record SerializedNotebook(string Text, ImmutableArray<CellLayout> Cells)
{
    public CellLayout? Find(string key) => Cells.FirstOrDefault(c => c.Key == key);
}

public static class NotebookWriter
{
    /// <summary>Writes the document (without a BOM) and records where each cell's content landed.</summary>
    public static SerializedNotebook Write(NotebookDocument doc)
    {
        var w = new TextBuilder();
        var nl = doc.Newline;
        foreach (var line in doc.Preamble.Lines) w.Append(line.Text + line.Terminator);

        var layouts = ImmutableArray.CreateBuilder<CellLayout>(doc.Cells.Length);
        for (var i = 0; i < doc.Cells.Length; i++)
        {
            var cell = doc.Cells[i];
            var raw = cell.Raw;
            var last = i == doc.Cells.Length - 1;

            w.EnsureLineStart(nl);
            var markerLine = w.Line;
            if (raw is not null && raw.SameMarker(cell)) w.Append(raw.MarkerLine);
            else w.Append(CellMarker.Format(cell) + nl);

            var lead = raw?.LeadingPad ?? "";
            string content;
            if (raw is not null && raw.SameContent(cell)) content = raw.Content;
            else content = RenderContent(cell, nl);
            var trail = raw?.TrailingPad ?? (last ? "" : nl);
            // A cell that used to be last has no blank line after it. Add one now that it isn't.
            if (!last && raw is not null && raw.WasLast && !trail.Contains('\n')) trail += nl;
            // And one that has just become last shouldn't leave blank lines at the end of the file.
            if (last && raw is not null && !raw.WasLast) trail = "";

            // Line breaks are only added in front of text that follows, so a file that ends
            // without a newline still round-trips.
            w.AppendOnNewLine(lead, nl);
            if (content.Length > 0) w.EnsureLineStart(nl);
            var contentStartLine = w.Line;
            var contentStart = w.Length;
            w.Append(content);
            var contentLength = w.Length - contentStart - TrailingNewlineLength(content);
            var contentLines = content.Length == 0 ? 0 : content.Count(c => c == '\n') + (content.EndsWith('\n') ? 0 : 1);
            w.AppendOnNewLine(trail, nl);

            layouts.Add(new CellLayout(i, cell.Key, cell.Kind, markerLine, contentStartLine, contentLines, contentStart, contentLength));
        }
        return new SerializedNotebook(w.ToString(), layouts.ToImmutable());
    }

    static string RenderContent(NotebookCell cell, string nl)
    {
        if (cell.Source.Length == 0) return "";
        var lines = cell.Source.Split('\n');
        if (cell.Kind == CellKind.Markdown)
            lines = [.. lines.Select(l => l.Length == 0 ? "//" : "// " + l)];
        return string.Concat(lines.Select(l => l + nl));
    }

    static int TrailingNewlineLength(string content) =>
        content.EndsWith("\r\n", StringComparison.Ordinal) ? 2 : content.EndsWith('\n') ? 1 : 0;

    sealed class TextBuilder
    {
        readonly StringBuilder sb = new();
        public int Line { get; private set; }
        public int Length => sb.Length;

        public void Append(string s)
        {
            sb.Append(s);
            foreach (var c in s) if (c == '\n') Line++;
        }

        public void EnsureLineStart(string nl)
        {
            if (sb.Length > 0 && sb[^1] != '\n') Append(nl);
        }

        public void AppendOnNewLine(string s, string nl)
        {
            if (s.Length == 0) return;
            EnsureLineStart(nl);
            Append(s);
        }

        public override string ToString() => sb.ToString();
    }
}
