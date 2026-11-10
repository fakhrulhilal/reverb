using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Reverb.Format;

public enum CellKind
{
    Setup,
    Code,
    Markdown,
    Types,
}

/// <summary>A marker attribute: a bare <c>key</c> or <c>key=value</c>.</summary>
public sealed record CellAttribute(string Key, string? Value = null);

/// <summary>A problem found while parsing the file. <see cref="Line"/> is 1-based.</summary>
public sealed record FormatDiagnostic(string Code, string Message, int Line);

/// <summary>
/// One cell. <see cref="Source"/> always uses <c>\n</c> line endings and has no leading or
/// trailing blank lines. For Markdown cells it's the Markdown text without the <c>//</c> prefixes.
/// </summary>
public sealed record NotebookCell
{
    public required CellKind Kind { get; init; }

    /// <summary>The <c>id=</c> attribute. Null when missing; <see cref="NotebookUpgrader"/> fills it in.</summary>
    public string? Id { get; init; }

    public string Source { get; init; } = "";

    /// <summary>Marker attributes other than <c>id</c>, in file order.</summary>
    public ImmutableArray<CellAttribute> Attributes { get; init; } = [];

    /// <summary>What the cell looked like in the parsed file. Lets unchanged cells be written back exactly.</summary>
    internal RawCell? Raw { get; init; }

    /// <summary>The id used to refer to the cell: "setup", "types", or the <c>id=</c> value.</summary>
    public string? Key => Kind switch
    {
        CellKind.Setup => "setup",
        CellKind.Types => "types",
        _ => Id,
    };

    public string? Name => GetAttribute("name");
    public bool HideCode => HasFlag("hide_code");
    public bool Disabled => HasFlag("disabled");

    public string? GetAttribute(string key) => Attributes.FirstOrDefault(a => a.Key == key)?.Value;
    public bool HasFlag(string key) => Attributes.Any(a => a.Key == key);

    public NotebookCell WithAttribute(string key, string? value)
    {
        var index = Attributes.IndexOf(Attributes.FirstOrDefault(a => a.Key == key)!);
        var attribute = new CellAttribute(key, value);
        return this with { Attributes = index >= 0 ? Attributes.SetItem(index, attribute) : Attributes.Add(attribute) };
    }

    public NotebookCell WithoutAttribute(string key) =>
        this with { Attributes = Attributes.RemoveAll(a => a.Key == key) };

    public NotebookCell WithFlag(string key, bool on) =>
        on ? (HasFlag(key) ? this : WithAttribute(key, null)) : WithoutAttribute(key);

    public static NotebookCell Code(string? id, string source) => new() { Kind = CellKind.Code, Id = id, Source = source };
    public static NotebookCell Markdown(string? id, string source) => new() { Kind = CellKind.Markdown, Id = id, Source = source };
    public static NotebookCell Setup(string source) => new() { Kind = CellKind.Setup, Source = source };
    public static NotebookCell Types(string source) => new() { Kind = CellKind.Types, Source = source };
}

/// <summary>The exact text of a parsed cell, plus the values it parsed to.</summary>
internal sealed record RawCell(
    string MarkerLine,
    string LeadingPad,
    string Content,
    string TrailingPad,
    bool WasLast,
    CellKind Kind,
    string? Id,
    ImmutableArray<CellAttribute> Attributes,
    string Source,
    int MarkerLineNumber)
{
    public bool SameMarker(NotebookCell cell) =>
        cell.Kind == Kind && cell.Id == Id && cell.Attributes.SequenceEqual(Attributes);

    public bool SameContent(NotebookCell cell) => cell.Kind == Kind && cell.Source == Source;
}

/// <summary>A <c>#:</c> directive, e.g. <c>#:package Humanizer@2.14.1</c> or <c>#:property LangVersion=preview</c>.</summary>
public sealed record Directive(string Kind, string Name, string? Value)
{
    public static bool TryParse(string line, out Directive directive)
    {
        directive = null!;
        if (!line.StartsWith("#:", StringComparison.Ordinal)) return false;
        var body = line[2..].Trim();
        var space = body.IndexOfAny([' ', '\t']);
        var kind = space < 0 ? body : body[..space];
        var argument = space < 0 ? "" : body[(space + 1)..].Trim();
        var separator = kind switch
        {
            "package" or "sdk" => '@',
            "property" => '=',
            _ => '\0',
        };
        var at = separator == '\0' ? -1 : argument.IndexOf(separator);
        directive = at < 0
            ? new Directive(kind, argument, null)
            : new Directive(kind, argument[..at], argument[(at + 1)..]);
        return true;
    }

    public override string ToString() => Value is null
        ? $"#:{Kind} {Name}"
        : $"#:{Kind} {Name}{(Kind == "property" ? '=' : '@')}{Value}";
}

/// <summary>A <c>#:package</c> line: a NuGet package and the version the notebook asks for.</summary>
public sealed record NotebookPackage(string Name, string Version)
{
    public override string ToString() => $"{Name}@{Version}";

    /// <summary>Why this can't be written as a <c>#:package</c> line, or null if it can.</summary>
    public string? Problem()
    {
        if (Name.Length == 0 || !Name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            return $"'{Name}' isn't a valid package name. NuGet package names use letters, digits, '.', '_' and '-'.";
        if (Version.Length == 0)
            return $"Package '{Name}' needs a version, e.g. 1.2.3 ('*' takes the latest).";
        if (!Version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+' or '*' or '[' or ']' or '(' or ')' or ','))
            return $"'{Version}' isn't a valid version for package '{Name}'.";
        return null;
    }
}

/// <summary>The <c>// reverb-notebook v1</c> line, with any flags after the version.</summary>
public sealed record NotebookHeader(int Version, ImmutableArray<string> Flags)
{
    static readonly Regex Pattern = new(@"^// reverb-notebook v(?<v>\d+)(?<flags>(?:[ \t]+[A-Za-z0-9_-]+)*)[ \t]*$", RegexOptions.CultureInvariant);

    public const int CurrentVersion = 1;
    public static readonly string CurrentLine = $"// reverb-notebook v{CurrentVersion}";

    public bool Lazy => Flags.Contains("lazy");

    public static NotebookHeader? TryParse(string line)
    {
        var m = Pattern.Match(line);
        if (!m.Success) return null;
        var flags = m.Groups["flags"].Value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return new NotebookHeader(int.Parse(m.Groups["v"].Value, System.Globalization.CultureInfo.InvariantCulture), [.. flags]);
    }
}

public sealed record PreambleLine(string Text, string Terminator);

/// <summary>Everything before the first cell: shebang, <c>#:</c> directives, the header comment, other comments.</summary>
public sealed record Preamble(ImmutableArray<PreambleLine> Lines)
{
    public static readonly Preamble Empty = new(ImmutableArray<PreambleLine>.Empty);

    public string? Shebang => Lines.Length > 0 && Lines[0].Text.StartsWith("#!", StringComparison.Ordinal) ? Lines[0].Text : null;

    public ImmutableArray<Directive> Directives =>
        [.. Lines.Select(l => Directive.TryParse(l.Text, out var d) ? d : null).OfType<Directive>()];

    public NotebookHeader? Header => Lines.Select(l => NotebookHeader.TryParse(l.Text)).FirstOrDefault(h => h is not null);

    public int HeaderIndex => Lines.IndexOf(Lines.FirstOrDefault(l => NotebookHeader.TryParse(l.Text) is not null)!);

    internal static bool IsDirective(PreambleLine line) => line.Text.StartsWith("#:", StringComparison.Ordinal);
}
