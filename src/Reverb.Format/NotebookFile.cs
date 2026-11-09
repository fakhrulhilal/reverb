using System.Text;

namespace Reverb.Format;

/// <summary>Reads and writes notebook files as UTF-8, keeping a byte order mark if there was one.</summary>
public static class NotebookFile
{
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static NotebookDocument Load(string path) => NotebookDocument.Parse(ReadText(path));

    /// <summary>The file's text with a leading U+FEFF if it starts with a BOM (unlike File.ReadAllText).</summary>
    public static string ReadText(string path)
    {
        try
        {
            return Utf8.GetString(File.ReadAllBytes(path));
        }
        catch (DecoderFallbackException e)
        {
            throw new InvalidDataException($"'{path}' isn't valid UTF-8. Save it as UTF-8 and open it again.", e);
        }
    }

    /// <summary>Writes to a temporary file next to the target, then renames it over the target.</summary>
    public static void Save(string path, NotebookDocument doc) => WriteTextAtomic(path, doc.Serialize());

    public static void WriteTextAtomic(string path, string text)
    {
        var full = Path.GetFullPath(path);
        var temp = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, Utf8.GetBytes(text));
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
