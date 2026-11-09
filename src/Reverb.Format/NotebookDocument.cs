using System.Collections.Immutable;

namespace Reverb.Format;

/// <summary>
/// A notebook file. Immutable: edits return a new document. Cells that weren't changed are
/// written back exactly as they were read, so <c>Parse(text).Serialize() == text</c> for any
/// file Reverb wrote.
/// </summary>
public sealed record NotebookDocument
{
    /// <summary>The file's line ending, taken from its first line break (<c>\n</c> for new files).</summary>
    public string Newline { get; init; } = "\n";

    public bool HasBom { get; init; }

    public Preamble Preamble { get; init; } = Preamble.Empty;

    public ImmutableArray<NotebookCell> Cells { get; init; } = [];

    /// <summary>Problems found by <see cref="Parse"/>. Not updated by edits.</summary>
    public ImmutableArray<FormatDiagnostic> Diagnostics { get; init; } = [];

    public static NotebookDocument Parse(string text) => NotebookParser.Parse(text);

    /// <summary>The full file text, including the byte order mark if the file had one.</summary>
    public string Serialize() => (HasBom ? "﻿" : "") + NotebookWriter.Write(this).Text;

    public NotebookCell? SetupCell => Cells.FirstOrDefault(c => c.Kind == CellKind.Setup);
    public NotebookCell? TypesCell => Cells.FirstOrDefault(c => c.Kind == CellKind.Types);

    public int IndexOf(string key)
    {
        for (var i = 0; i < Cells.Length; i++)
            if (Cells[i].Key == key) return i;
        return -1;
    }

    public NotebookCell? FindCell(string key) => IndexOf(key) is var i and >= 0 ? Cells[i] : null;

    public NotebookDocument ReplaceCell(int index, NotebookCell cell) => this with { Cells = Cells.SetItem(index, cell) };

    public NotebookDocument WithCellSource(string key, string source)
    {
        var index = RequireIndex(key);
        return ReplaceCell(index, Cells[index] with { Source = NormalizeSource(source) });
    }

    public NotebookDocument InsertCell(int index, NotebookCell cell) =>
        this with { Cells = Cells.Insert(index, cell with { Source = NormalizeSource(cell.Source) }) };

    public NotebookDocument RemoveCell(int index) => this with { Cells = Cells.RemoveAt(index) };

    /// <summary>Moves the cell at <paramref name="from"/> so it ends up at index <paramref name="to"/>.</summary>
    public NotebookDocument MoveCell(int from, int to)
    {
        var cell = Cells[from];
        return this with { Cells = Cells.RemoveAt(from).Insert(to, cell) };
    }

    /// <summary>
    /// Replaces all <c>#:</c> directives. New directives go where the old ones were, or right after
    /// the shebang if there were none.
    /// </summary>
    public NotebookDocument WithDirectives(IEnumerable<Directive> directives)
    {
        var lines = Preamble.Lines;
        var insertAt = lines.IndexOf(lines.FirstOrDefault(Preamble.IsDirective)!);
        if (insertAt < 0) insertAt = Preamble.Shebang is null ? 0 : 1;
        var kept = lines.Where(l => !Preamble.IsDirective(l)).ToList();
        var removedBefore = lines.Take(insertAt).Count(Preamble.IsDirective);
        kept.InsertRange(insertAt - removedBefore, directives.Select(d => new PreambleLine(d.ToString(), Newline)));
        return this with { Preamble = new Preamble([.. kept]) };
    }

    /// <summary>
    /// Turns a flag on the <c>// reverb-notebook v1</c> header line on or off (for example <c>lazy</c>).
    /// Adds the header if the file doesn't have one.
    /// </summary>
    public NotebookDocument WithHeaderFlag(string flag, bool on)
    {
        if (flag.Length == 0 || !flag.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            throw new ArgumentException($"'{flag}' isn't a valid header flag. Use letters, digits, '-' and '_' only.", nameof(flag));

        var lines = Preamble.Lines.ToList();
        var index = Preamble.HeaderIndex;
        var header = Preamble.Header ?? new NotebookHeader(NotebookHeader.CurrentVersion, []);
        if (header.Flags.Contains(flag) == on && index >= 0) return this;

        var flags = on ? header.Flags.Remove(flag).Add(flag) : header.Flags.Remove(flag);
        var text = string.Join(' ', [$"// reverb-notebook v{header.Version}", .. flags]);
        if (index >= 0)
        {
            lines[index] = lines[index] with { Text = text };
        }
        else
        {
            var at = lines.FindLastIndex(l => Preamble.IsDirective(l) || l.Text.StartsWith("#!", StringComparison.Ordinal)) + 1;
            lines.Insert(at, new PreambleLine(text, Newline));
        }
        return this with { Preamble = new Preamble([.. lines]) };
    }

    int RequireIndex(string key) =>
        IndexOf(key) is var i and >= 0 ? i : throw new KeyNotFoundException($"There is no cell '{key}' in this notebook.");

    /// <summary>Turns editor text into cell source: <c>\n</c> line endings, no blank lines at either end.</summary>
    public static string NormalizeSource(string source)
    {
        var lines = source.ReplaceLineEndings("\n").Split('\n');
        var start = 0;
        var end = lines.Length;
        while (start < end && string.IsNullOrWhiteSpace(lines[start])) start++;
        while (end > start && string.IsNullOrWhiteSpace(lines[end - 1])) end--;
        return string.Join('\n', lines[start..end]);
    }
}
