using System.Collections.Immutable;

namespace Reverb.Format;

internal static class NotebookParser
{
    internal readonly record struct Line(string Text, string Terminator)
    {
        public string Raw => Text + Terminator;
        public bool IsBlank => string.IsNullOrWhiteSpace(Text);
    }

    public static NotebookDocument Parse(string text)
    {
        var hasBom = text.StartsWith('﻿');
        if (hasBom) text = text[1..];

        var lines = SplitLines(text);
        var newline = lines.Select(l => l.Terminator).FirstOrDefault(t => t.Length > 0) ?? "\n";
        var diagnostics = ImmutableArray.CreateBuilder<FormatDiagnostic>();

        var markers = new List<int>();
        for (var i = 0; i < lines.Count; i++)
            if (CellMarker.IsMarker(lines[i].Text)) markers.Add(i);
        var firstMarker = markers.Count > 0 ? markers[0] : lines.Count;

        // Preamble: shebang, directives, comments and blank lines up to the first code or marker.
        var p = 0;
        while (p < firstMarker && IsPreambleLine(lines[p], p)) p++;
        var preamble = new Preamble([.. lines.Take(p).Select(l => new PreambleLine(l.Text, l.Terminator))]);

        var cells = new List<NotebookCell>();
        if (p < firstMarker)
        {
            // Code with no marker in front of it: a hand-written file, or code above the first marker.
            var implicitText = string.Concat(lines.Skip(p).Take(firstMarker - p).Select(l => l.Raw));
            cells.AddRange(MarkerlessImporter.Split(implicitText));
        }

        for (var k = 0; k < markers.Count; k++)
        {
            var start = markers[k];
            var end = k + 1 < markers.Count ? markers[k + 1] : lines.Count;
            cells.AddRange(ParseCell(lines, start, end, isLast: k == markers.Count - 1, diagnostics));
        }

        CheckIds(cells, lines, markers, diagnostics);
        CheckOrder(cells, diagnostics);

        return new NotebookDocument
        {
            Newline = newline,
            HasBom = hasBom,
            Preamble = preamble,
            Cells = [.. cells],
            Diagnostics = diagnostics.ToImmutable(),
        };
    }

    static bool IsPreambleLine(Line line, int index)
    {
        if (line.IsBlank) return true;
        var t = line.Text.TrimStart();
        return (index == 0 && t.StartsWith("#!", StringComparison.Ordinal))
            || t.StartsWith("#:", StringComparison.Ordinal)
            || t.StartsWith("//", StringComparison.Ordinal);
    }

    static IEnumerable<NotebookCell> ParseCell(List<Line> lines, int markerIndex, int end, bool isLast,
        ImmutableArray<FormatDiagnostic>.Builder diagnostics)
    {
        var markerLine = lines[markerIndex];
        var marker = CellMarker.Parse(markerLine.Text);
        foreach (var problem in marker.Problems)
            diagnostics.Add(new FormatDiagnostic("RVB103", problem, markerIndex + 1));

        var body = lines.Skip(markerIndex + 1).Take(end - markerIndex - 1).ToList();
        var (lead, content, trail) = SplitPadding(body);

        NotebookCell? split = null;
        if (marker.Kind == CellKind.Markdown)
        {
            // A Markdown cell is comment lines only. If code follows, it starts a new code cell.
            var codeAt = content.FindIndex(l => !l.IsBlank && !l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal));
            if (codeAt >= 0)
            {
                diagnostics.Add(new FormatDiagnostic("RVB104",
                    "This Markdown cell contains a line that isn't a // comment, so the code from that line on was treated as a separate code cell. Put a '// %% id=...' marker above the code.",
                    markerIndex + 2 + lead.Count + codeAt));
                var codeLines = content.Skip(codeAt).Concat(trail).ToList();
                var mdLines = content.Take(codeAt).ToList();
                (_, var mdContent, var mdTrail) = SplitPadding(mdLines);
                content = mdContent;
                trail = mdTrail;
                var (_, code, _) = SplitPadding(codeLines);
                split = NotebookCell.Code(null, JoinSource(code));
                isLast = false;
            }
        }

        var source = marker.Kind == CellKind.Markdown
            ? string.Join('\n', content.Select(l => StripComment(l.Text)))
            : JoinSource(content);

        var raw = new RawCell(
            markerLine.Raw,
            string.Concat(lead.Select(l => l.Raw)),
            string.Concat(content.Select(l => l.Raw)),
            string.Concat(trail.Select(l => l.Raw)),
            isLast,
            marker.Kind,
            marker.Id,
            marker.Attributes,
            source,
            markerIndex + 1);

        yield return new NotebookCell
        {
            Kind = marker.Kind,
            Id = marker.Id,
            Attributes = marker.Attributes,
            Source = source,
            Raw = raw,
        };
        if (split is not null) yield return split;
    }

    static void CheckIds(List<NotebookCell> cells, List<Line> lines, List<int> markers, ImmutableArray<FormatDiagnostic>.Builder diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (cell.Id is null) continue;
            var line = cell.Raw?.MarkerLineNumber ?? 0;
            if (!CellIds.IsValid(cell.Id) || cell.Kind is CellKind.Code or CellKind.Markdown && CellIds.IsReserved(cell.Id))
            {
                diagnostics.Add(new FormatDiagnostic("RVB102",
                    $"'{cell.Id}' isn't a valid cell id. Ids are 1 to 32 characters of a-z, 0-9, '_' or '-', and can't be 'setup' or 'types'. A new id will be assigned when the notebook is saved.",
                    line));
                cells[i] = Clear(cell);
            }
            else if (!seen.Add(cell.Id))
            {
                diagnostics.Add(new FormatDiagnostic("RVB101",
                    $"Cell id '{cell.Id}' is used more than once. This cell will get a new id when the notebook is saved.",
                    line));
                cells[i] = Clear(cell);
            }
        }

        // Clearing the id must not make the cell look edited, or its marker would be rewritten
        // even when nothing else changes. Keep the raw snapshot in step.
        static NotebookCell Clear(NotebookCell c) => c with { Id = null, Raw = c.Raw is null ? null : c.Raw with { Id = null } };
    }

    static void CheckOrder(List<NotebookCell> cells, ImmutableArray<FormatDiagnostic>.Builder diagnostics)
    {
        var firstCode = cells.FindIndex(c => c.Kind == CellKind.Code);
        var lastCode = cells.FindLastIndex(c => c.Kind == CellKind.Code);
        for (var i = 0; i < cells.Count; i++)
        {
            var kind = cells[i].Kind;
            if (kind == CellKind.Setup && firstCode >= 0 && i > firstCode)
                diagnostics.Add(new FormatDiagnostic("RVB105", "The setup cell must come before all code cells, because C# needs using directives before any statements.", 0));
            if (kind == CellKind.Types && lastCode >= 0 && i < lastCode)
                diagnostics.Add(new FormatDiagnostic("RVB105", "The types cell must come after all code cells, because C# needs type declarations after all top-level statements.", 0));
        }
        if (cells.Count(c => c.Kind == CellKind.Setup) > 1)
            diagnostics.Add(new FormatDiagnostic("RVB105", "There is more than one setup cell. Merge them into one.", 0));
        if (cells.Count(c => c.Kind == CellKind.Types) > 1)
            diagnostics.Add(new FormatDiagnostic("RVB105", "There is more than one types cell. Merge them into one.", 0));
    }

    internal static (List<Line> Lead, List<Line> Content, List<Line> Trail) SplitPadding(List<Line> body)
    {
        var start = 0;
        while (start < body.Count && body[start].IsBlank) start++;
        if (start == body.Count) return ([], [], body); // all padding counts as trailing
        var end = body.Count;
        while (end > start && body[end - 1].IsBlank) end--;
        return (body[..start], body[start..end], body[end..]);
    }

    static string JoinSource(List<Line> content) => string.Join('\n', content.Select(l => l.Text));

    static string StripComment(string line)
    {
        var t = line.TrimStart();
        if (!t.StartsWith("//", StringComparison.Ordinal)) return "";
        t = t[2..];
        return t.StartsWith(' ') ? t[1..] : t;
    }

    internal static List<Line> SplitLines(string text)
    {
        var lines = new List<Line>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            var crlf = i > start && text[i - 1] == '\r';
            lines.Add(new Line(text[start..(crlf ? i - 1 : i)], crlf ? "\r\n" : "\n"));
            start = i + 1;
        }
        if (start < text.Length) lines.Add(new Line(text[start..], ""));
        return lines;
    }
}
