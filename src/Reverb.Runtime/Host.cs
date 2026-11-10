namespace Reverb;

/// <summary>
/// Where notebook output goes. The Reverb kernel sets <see cref="Current"/> before it runs cells.
/// When nothing is set (plain <c>dotnet run</c>), output is written to the console as text.
/// </summary>
public abstract class ReverbHost
{
    static ReverbHost? current;

    public static ReverbHost? Current
    {
        get => current;
        set => current = value;
    }

    internal static ReverbHost Effective => current ?? ConsoleHost.Instance;

    public abstract void Display(object? value, string? title);

    public abstract void Markdown(string markdown);

    public abstract void Html(string html);

    /// <summary>Called when a widget is created. A live host can restore the value the user picked.</summary>
    public virtual void WidgetCreated(Widget widget)
    {
    }

    /// <summary>Signalled when the user interrupts the running cell. Never signalled under <c>dotnet run</c>.</summary>
    public virtual CancellationToken CancellationToken => CancellationToken.None;

    /// <summary>For hosts: gives a widget the id user changes will refer to.</summary>
    protected static void AssignWidgetId(Widget widget, string id) => widget.Id = id;
}

/// <summary>Writes output as plain text. Used when a notebook runs with <c>dotnet run</c>.</summary>
public sealed class ConsoleHost : ReverbHost
{
    public static readonly ConsoleHost Instance = new();

    public override void Display(object? value, string? title)
    {
        if (title is not null) Console.WriteLine(TitleLine(title));
        Console.WriteLine(Formatters.Format(value).Text);
    }

    /// <summary>How a display title is shown as text.</summary>
    public static string TitleLine(string title) => $"== {title} ==";

    public override void Markdown(string markdown) => Console.WriteLine(markdown);

    public override void Html(string html) => Console.WriteLine(html);
}

public static class DumpExtensions
{
    /// <summary>Displays the value and returns it, so it can sit at the end of a chain.</summary>
    public static T Dump<T>(this T value, string? title = null)
    {
        ReverbHost.Effective.Display(value, title);
        return value;
    }
}

/// <summary>Output helpers. Add <c>using static Reverb.Notebook;</c> to call them as <c>Md(...)</c>.</summary>
public static class Notebook
{
    public static void Display(object? value) => ReverbHost.Effective.Display(value, null);

    public static void Md(string markdown) => ReverbHost.Effective.Markdown(markdown);

    public static void Html(string html) => ReverbHost.Effective.Html(html);

    /// <summary>
    /// Cancelled when the user interrupts the running cell. Pass it to long waits, e.g.
    /// <c>await Task.Delay(5000, Notebook.CancellationToken)</c>, so interrupting works.
    /// </summary>
    public static CancellationToken CancellationToken => ReverbHost.Effective.CancellationToken;

    /// <summary>
    /// The folder the notebook file is in, for data files that sit next to it:
    /// <c>File.ReadAllLines(Path.Combine(Notebook.Folder, "data.csv"))</c>. <c>dotnet run</c> and the
    /// kernel both say where the file is; failing that, it's the current directory.
    /// </summary>
    public static string Folder => AppContext.GetData("EntryPointFileDirectoryPath") as string is { Length: > 0 } dir ? dir : Environment.CurrentDirectory;
}
