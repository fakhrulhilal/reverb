using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reverb.Kernel;

/// <summary>
/// Messages between a client (CLI or server) and the kernel process: one JSON object per line on
/// stdin/stdout. Kernel lines start with <see cref="LinePrefix"/> so stray output written straight
/// to the process's stdout (by native code, say) can't be mistaken for a message.
/// </summary>
public static class Protocol
{
    public const string LinePrefix = "@@reverb ";

    public static string Encode(KernelRequest request) => JsonSerializer.Serialize(request, ProtocolJson.Default.KernelRequest);
    public static string Encode(KernelEvent e) => LinePrefix + JsonSerializer.Serialize(e, ProtocolJson.Default.KernelEvent);

    public static KernelRequest? DecodeRequest(string line) => JsonSerializer.Deserialize(line, ProtocolJson.Default.KernelRequest);

    public static KernelEvent? DecodeEvent(string line) =>
        line.StartsWith(LinePrefix, StringComparison.Ordinal)
            ? JsonSerializer.Deserialize(line.AsSpan(LinePrefix.Length), ProtocolJson.Default.KernelEvent)
            : null;
}

public static class RequestTypes
{
    public const string Open = "open";
    public const string Update = "update";
    public const string Run = "run";
    public const string RunAll = "runAll";
    public const string SetWidget = "setWidget";
    public const string SetLazy = "setLazy";

    /// <summary>Analyse a version of the notebook without running it. Answered even while a cell runs.</summary>
    public const string Analyze = "analyze";
    public const string Interrupt = "interrupt";

    // Language services. Answered on their own worker, even while a cell runs; the answer is in the done event.
    public const string Complete = "complete";
    public const string Hover = "hover";
    public const string SignatureHelp = "signatureHelp";
    public const string CodeFix = "codeFix";
    public const string Shutdown = "shutdown";
}

public static class EventTypes
{
    public const string Ready = "ready";
    public const string Done = "done";
    public const string Status = "cell.status";
    public const string Cleared = "cell.cleared";
    public const string Stream = "cell.stream";
    public const string Display = "cell.display";
    public const string Outputs = "cell.outputs";
    public const string Diagnostics = "notebook.diagnostics";

    /// <summary>The answer to <see cref="RequestTypes.Analyze"/>: diagnostics and the dependency graph.</summary>
    public const string Analysis = "notebook.analysis";
    public const string Error = "kernel.error";
}

public sealed class KernelRequest
{
    public string Type { get; set; } = "";
    public int Id { get; set; }
    public string? Path { get; set; }
    public string? Text { get; set; }
    public string[]? Args { get; set; }
    public bool? Lazy { get; set; }
    public bool? Run { get; set; }
    public string? Cell { get; set; }
    public string? WidgetId { get; set; }
    public JsonElement? Value { get; set; }

    /// <summary>The caller's version number for <see cref="Text"/>, echoed back in the answer.</summary>
    public int? Version { get; set; }

    /// <summary>For language requests: a position in <see cref="Cell"/>'s source.</summary>
    public int? Offset { get; set; }

    /// <summary>For codeFix: which diagnostic, by id and where it starts in the cell.</summary>
    public string? DiagnosticId { get; set; }
    public int? Line { get; set; }
    public int? Column { get; set; }
}

public sealed class KernelEvent
{
    public string Type { get; set; } = "";
    public int? RequestId { get; set; }
    public bool? Ok { get; set; }
    public string? Cell { get; set; }
    public string? Status { get; set; }
    public long? ElapsedMs { get; set; }
    public string? Stream { get; set; }
    public string? Text { get; set; }
    public string? Message { get; set; }
    public OutputData? Output { get; set; }
    public List<OutputData>? Outputs { get; set; }
    public List<DiagnosticData>? Diagnostics { get; set; }
    public List<EdgeData>? Graph { get; set; }
    public int? Version { get; set; }

    // Answers to language requests, in their done event. From/To are offsets in the cell's source.
    public int? From { get; set; }
    public int? To { get; set; }
    public List<CompletionData>? Completions { get; set; }
    public List<SignatureData>? Signatures { get; set; }
    public int? ActiveSignature { get; set; }
    public int? ActiveParameter { get; set; }
}

public sealed class CompletionData
{
    public string Label { get; set; } = "";

    /// <summary>Text to insert when it differs from the label (e.g. a generic method's label has &lt;&gt;).</summary>
    public string? Insert { get; set; }

    /// <summary>variable, method, property, field, class, struct, interface, enum, namespace, keyword, ...</summary>
    public string Kind { get; set; } = "";
    public string? Detail { get; set; }
    public string SortText { get; set; } = "";
}

public sealed class SignatureData
{
    public string Label { get; set; } = "";
    public List<string> Parameters { get; set; } = [];
}

/// <summary>Cell <see cref="From"/> uses the names in <see cref="Via"/> from cell <see cref="To"/>.</summary>
public sealed class EdgeData
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public List<string> Via { get; set; } = [];
}

/// <summary>An output in a message. Stream text appears as an output with <see cref="Stream"/> set.</summary>
public sealed class OutputData
{
    public Dictionary<string, string>? Data { get; set; }
    public string? Title { get; set; }
    public string? Stream { get; set; }
    public string? Text { get; set; }

    public static OutputData From(CellOutputItem item) => item switch
    {
        StreamItem s => new OutputData { Stream = s.Stream, Text = s.Text },
        DisplayItem d => From(d.Output),
        _ => throw new ArgumentOutOfRangeException(nameof(item)),
    };

    public static OutputData From(Output o) => new() { Data = new(o.Data), Title = o.Title };
}

public sealed class DiagnosticData
{
    public string Id { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Cell { get; set; }
    public int StartLine { get; set; }
    public int StartColumn { get; set; }
    public int EndLine { get; set; }
    public int EndColumn { get; set; }

    /// <summary>What Reverb's fix for this diagnostic does, when it has one ("Move to the setup cell").</summary>
    public string? Fix { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(KernelRequest))]
[JsonSerializable(typeof(KernelEvent))]
public sealed partial class ProtocolJson : JsonSerializerContext;
