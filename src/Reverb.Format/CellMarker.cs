using System.Collections.Immutable;
using System.Text;

namespace Reverb.Format;

/// <summary>Reads and writes <c>// %%</c> marker lines.</summary>
public static class CellMarker
{
    public const string Prefix = "// %%";

    /// <summary>A marker starts at column 0 with <c>// %%</c>, followed by whitespace or the end of the line.</summary>
    public static bool IsMarker(string line) =>
        line.StartsWith(Prefix, StringComparison.Ordinal)
        && (line.Length == Prefix.Length || line[Prefix.Length] is ' ' or '\t');

    public sealed record Parsed(CellKind Kind, string? Id, ImmutableArray<CellAttribute> Attributes, ImmutableArray<string> Problems);

    public static Parsed Parse(string line)
    {
        var problems = ImmutableArray.CreateBuilder<string>();
        var tokens = Tokenize(line[Prefix.Length..], problems);
        var kind = CellKind.Code;
        var start = 0;
        if (tokens.Count > 0)
        {
            switch (tokens[0].ToLowerInvariant())
            {
                case "setup": kind = CellKind.Setup; start = 1; break;
                case "types": kind = CellKind.Types; start = 1; break;
                case "[markdown]" or "[md]": kind = CellKind.Markdown; start = 1; break;
            }
        }

        string? id = null;
        var attributes = ImmutableArray.CreateBuilder<CellAttribute>();
        foreach (var token in tokens.Skip(start))
        {
            var eq = token.IndexOf('=');
            var key = eq < 0 ? token : token[..eq];
            var value = eq < 0 ? null : Unquote(token[(eq + 1)..]);
            if (key == "id" && id is null && value is not null) id = value;
            else attributes.Add(new CellAttribute(key, value));
        }
        return new Parsed(kind, id, attributes.ToImmutable(), problems.ToImmutable());
    }

    /// <summary>The canonical marker for a cell: kind, then <c>id=</c>, then other attributes in order.</summary>
    public static string Format(NotebookCell cell)
    {
        var sb = new StringBuilder(Prefix);
        switch (cell.Kind)
        {
            case CellKind.Setup: sb.Append(" setup"); break;
            case CellKind.Types: sb.Append(" types"); break;
            case CellKind.Markdown: sb.Append(" [markdown]"); break;
        }
        if (cell.Id is not null && cell.Kind is CellKind.Code or CellKind.Markdown) sb.Append(" id=").Append(cell.Id);
        foreach (var a in cell.Attributes)
        {
            sb.Append(' ').Append(a.Key);
            if (a.Value is not null) sb.Append('=').Append(Quote(a.Value));
        }
        return sb.ToString();
    }

    static List<string> Tokenize(string text, ImmutableArray<string>.Builder problems)
    {
        // Tokens are separated by whitespace. Inside double quotes, whitespace is kept and
        // \" and \\ are escapes. Quotes are kept in the token; Unquote removes them.
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                current.Append(c);
                if (c == '\\' && i + 1 < text.Length) current.Append(text[++i]);
                else if (c == '"') inQuotes = false;
            }
            else if (c is ' ' or '\t')
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }
            else
            {
                current.Append(c);
                if (c == '"') inQuotes = true;
            }
        }
        if (inQuotes) problems.Add("A quoted value in this marker has no closing quote.");
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    static string Unquote(string value)
    {
        if (value.Length == 0 || value[0] != '"') return value;
        var sb = new StringBuilder();
        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length) sb.Append(value[++i]);
            else if (c == '"') break;
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static string Quote(string value)
    {
        var plain = value.Length > 0 && value.All(c => !char.IsWhiteSpace(c) && c is not '"' and not '\\' and not '=');
        return plain ? value : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
