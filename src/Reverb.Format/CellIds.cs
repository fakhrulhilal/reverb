using System.Text.RegularExpressions;

namespace Reverb.Format;

public static partial class CellIds
{
    const string Letters = "abcdefghijklmnopqrstuvwxyz";
    const string Alphabet = Letters + "0123456789";

    [GeneratedRegex("^[a-z0-9_-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsValid(string id) => Pattern().IsMatch(id);

    /// <summary>"setup" and "types" name the special cells, so code and Markdown cells can't use them.</summary>
    public static bool IsReserved(string id) => id is "setup" or "types";

    /// <summary>A new 6-character id (a letter, then letters and digits) not in <paramref name="taken"/>.</summary>
    public static string New(Random random, IReadOnlySet<string> taken)
    {
        Span<char> chars = stackalloc char[6];
        while (true)
        {
            chars[0] = Letters[random.Next(Letters.Length)];
            for (var i = 1; i < chars.Length; i++) chars[i] = Alphabet[random.Next(Alphabet.Length)];
            var id = new string(chars);
            if (!taken.Contains(id) && !IsReserved(id)) return id;
        }
    }
}
