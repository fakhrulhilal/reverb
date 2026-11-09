using System.Text;

namespace Reverb.Kernel;

/// <summary>Something a cell produced: console text or a displayed value, in the order it happened.</summary>
public abstract record CellOutputItem;

public sealed record StreamItem(string Stream, string Text) : CellOutputItem
{
    public const string Stdout = "stdout";
    public const string Stderr = "stderr";
}

public sealed record DisplayItem(Output Output) : CellOutputItem
{
    /// <summary>The widget this output shows, so it can be redrawn when the widget's value changes.</summary>
    public Widget? Widget { get; init; }
}

/// <summary>Where one run of one cell sends its output.</summary>
sealed class CellSink(string cellKey, KernelSession session, CancellationToken token)
{
    static readonly AsyncLocal<CellSink?> current = new();

    /// <summary>The sink of the cell whose code is running on this logical thread, if any.</summary>
    public static CellSink? Current
    {
        get => current.Value;
        set => current.Value = value;
    }

    readonly Lock gate = new();
    readonly StringBuilder pendingOut = new();
    readonly StringBuilder pendingErr = new();
    int widgetIndex;

    public string CellKey { get; } = cellKey;
    public KernelSession Session { get; } = session;
    public CancellationToken Token { get; } = token;
    public List<CellOutputItem> Items { get; } = [];
    public List<Widget> Widgets { get; } = [];

    public void Write(string stream, string text)
    {
        lock (gate)
        {
            var buffer = stream == StreamItem.Stdout ? pendingOut : pendingErr;
            var other = stream == StreamItem.Stdout ? pendingErr : pendingOut;
            // Switching streams: whatever the other stream has waiting came first.
            if (other.Length > 0)
            {
                AddStream(stream == StreamItem.Stdout ? StreamItem.Stderr : StreamItem.Stdout, other.ToString());
                other.Clear();
            }
            buffer.Append(text);
            // Send whole lines as they complete; the rest waits for more text or the end of the cell.
            var lastNewline = buffer.ToString().LastIndexOf('\n');
            if (lastNewline < 0) return;
            var complete = buffer.ToString(0, lastNewline + 1);
            buffer.Remove(0, lastNewline + 1);
            AddStream(stream, complete);
        }
    }

    public void Display(Output output, Widget? widget = null)
    {
        lock (gate)
        {
            FlushLocked();
            var item = new DisplayItem(output) { Widget = widget };
            Items.Add(item);
            Session.Events.CellDisplay(CellKey, output);
        }
    }

    public string NextWidgetId() => $"{CellKey}:{widgetIndex++}";

    public void Flush()
    {
        lock (gate) FlushLocked();
    }

    void FlushLocked()
    {
        if (pendingOut.Length > 0) { AddStream(StreamItem.Stdout, pendingOut.ToString()); pendingOut.Clear(); }
        if (pendingErr.Length > 0) { AddStream(StreamItem.Stderr, pendingErr.ToString()); pendingErr.Clear(); }
    }

    void AddStream(string stream, string text)
    {
        // Merge with the previous item when it's the same stream, so output stays compact.
        if (Items.Count > 0 && Items[^1] is StreamItem last && last.Stream == stream)
            Items[^1] = last with { Text = last.Text + text };
        else
            Items.Add(new StreamItem(stream, text));
        Session.Events.CellStream(CellKey, stream, text);
    }
}

/// <summary>
/// Replaces Console.Out/Console.Error. Text written while a cell runs goes to that cell; anything
/// else goes to the original writer.
/// </summary>
sealed class RoutingWriter(string stream, TextWriter fallback) : TextWriter
{
    public override Encoding Encoding => fallback.Encoding;

    public override void Write(char value) => Write(value.ToString());

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (CellSink.Current is { } sink) sink.Write(stream, value);
        else fallback.Write(value);
    }

    public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

    public override void WriteLine(string? value) => Write((value ?? "") + new string(CoreNewLine));

    public override void Flush()
    {
        if (CellSink.Current is null) fallback.Flush();
    }
}

/// <summary>The <see cref="ReverbHost"/> used inside the kernel. Sends output to the running cell.</summary>
sealed class KernelHost : ReverbHost
{
    public static readonly KernelHost Instance = new();
    static readonly Lock installGate = new();
    static bool installed;

    /// <summary>Routes Console and Reverb output through the kernel. Safe to call more than once.</summary>
    public static void Install()
    {
        lock (installGate)
        {
            if (installed) return;
            Console.SetOut(new RoutingWriter(StreamItem.Stdout, Console.Out));
            Console.SetError(new RoutingWriter(StreamItem.Stderr, Console.Error));
            // The process's stdin carries requests from the client; a cell reading the console
            // must not eat them. It sees an empty input instead.
            Console.SetIn(TextReader.Null);
            Current = Instance;
            installed = true;
        }
    }

    public override void Display(object? value, string? title) => Show(Formatters.Format(value).WithTitle(title));

    public override void Markdown(string markdown) => Show(Output.FromMarkdown(markdown));

    public override void Html(string html) => Show(Output.FromHtml(html));

    public override CancellationToken CancellationToken => CellSink.Current?.Token ?? CancellationToken.None;

    public override void WidgetCreated(Widget widget)
    {
        if (CellSink.Current is not { } sink) return;
        var id = sink.NextWidgetId();
        AssignWidgetId(widget, id);
        sink.Session.RestoreWidget(id, widget);
        sink.Widgets.Add(widget);
        sink.Display(Formatters.Format(widget), widget);
    }

    static void Show(Output output)
    {
        if (CellSink.Current is { } sink) sink.Display(output);
        else ConsoleHost.Instance.Display(output, output.Title);
    }
}
