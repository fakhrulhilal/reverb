using System.Text.Json;
using System.Text.Json.Serialization;
using Reverb.Kernel;

namespace Reverb.Server;

/// <summary>Message types between the browser and the server, over the WebSocket at /ws.</summary>
public static class BrowserMessages
{
    // browser -> server
    public const string CellUpdate = "cell.update";
    public const string CellRun = "cell.run";
    public const string RunAll = "notebook.runAll";
    public const string SetLazy = "notebook.setLazy";
    public const string Save = "notebook.save";
    public const string CellCreate = "cell.create";
    public const string CellDelete = "cell.delete";
    public const string CellMove = "cell.move";
    public const string CellSetAttrs = "cell.setAttrs";
    public const string WidgetSetValue = "widget.setValue";
    public const string KernelInterrupt = "kernel.interrupt";
    public const string KernelRestart = "kernel.restart";

    // server -> browser
    public const string Snapshot = "notebook.snapshot";
    public const string CellStatus = "cell.status";
    public const string CellOutputs = "cell.outputs";
    public const string CellStream = "cell.stream";
    public const string CellDiagnostics = "cell.diagnostics";
    public const string GraphUpdate = "graph.update";
    public const string KernelStatus = "kernel.status";
    public const string FileSaved = "file.saved";
    public const string FileExternalChange = "file.externalChange";
    public const string Error = "error";
}

/// <summary>A message from the browser. Which fields are set depends on <see cref="Type"/>.</summary>
public sealed class ClientMessage
{
    public string Type { get; set; } = "";

    /// <summary>Increases with every message a page sends. Snapshots echo the last one handled.</summary>
    public long Seq { get; set; }

    public string? CellId { get; set; }
    public string? Source { get; set; }
    public string? AfterCellId { get; set; }
    public string? Kind { get; set; }
    public CellAttrs? Attrs { get; set; }
    public bool? Lazy { get; set; }
    public string? WidgetId { get; set; }
    public JsonElement? Value { get; set; }
}

/// <summary>A message to the browser. Which fields are set depends on <see cref="Type"/>.</summary>
public sealed class ServerMessage
{
    public string Type { get; set; } = "";
    public int? NotebookVersion { get; set; }
    public long? Ack { get; set; }

    // notebook.snapshot
    public string? Path { get; set; }
    public string? Name { get; set; }
    public bool? Lazy { get; set; }
    public List<CellView>? Cells { get; set; }
    public List<PackageView>? Packages { get; set; }
    public KernelView? Kernel { get; set; }
    public List<DiagnosticData>? NotebookDiagnostics { get; set; }

    // cell messages
    public string? CellId { get; set; }
    public string? Status { get; set; }
    public long? ElapsedMs { get; set; }
    public string? Message { get; set; }
    public List<OutputData>? Outputs { get; set; }
    public string? Stream { get; set; }
    public string? Text { get; set; }
    public List<DiagnosticData>? Diagnostics { get; set; }

    // graph.update, and the graph in a snapshot
    public List<EdgeData>? Edges { get; set; }

    // kernel.status
    public string? State { get; set; }
}

public sealed class CellAttrs
{
    [JsonPropertyName("hide_code")]
    public bool? HideCode { get; set; }

    public bool? Disabled { get; set; }

    /// <summary>Empty string removes the name.</summary>
    public string? Name { get; set; }
}

public sealed class CellView
{
    public string Id { get; set; } = "";

    /// <summary>code, markdown, setup or types.</summary>
    public string Kind { get; set; } = "";

    public string Source { get; set; } = "";
    public CellAttrs Attrs { get; set; } = new();
    public string Status { get; set; } = "idle";
    public long? ElapsedMs { get; set; }
    public string? Message { get; set; }
    public List<OutputData> Outputs { get; set; } = [];
    public List<DiagnosticData> Diagnostics { get; set; } = [];

    /// <summary>The source differs from what the kernel last ran.</summary>
    public bool Edited { get; set; }
}

public sealed class PackageView
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
}

public sealed class KernelView
{
    /// <summary>starting, ready, busy or dead.</summary>
    public string State { get; set; } = "starting";

    public string? Message { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ClientMessage))]
[JsonSerializable(typeof(ServerMessage))]
public sealed partial class BrowserJson : JsonSerializerContext;
