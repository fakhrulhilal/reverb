// Messages between the browser and the Reverb server. Mirrors Reverb.Server/BrowserProtocol.cs.

export type CellKind = "code" | "markdown" | "setup" | "types";

export type CellStatus = "idle" | "queued" | "running" | "stale" | "error" | "upstream-error" | "disabled";

export type KernelState = "starting" | "ready" | "busy" | "dead";

export interface CellAttrs {
  hide_code?: boolean;
  disabled?: boolean;
  name?: string | null;
}

/** One output. Stream text has `stream` and `text`; anything else has MIME `data`. */
export interface Output {
  data?: Record<string, string>;
  title?: string;
  stream?: "stdout" | "stderr";
  text?: string;
}

/** Ranges are zero-based and relative to the cell. */
export interface Diagnostic {
  id: string;
  severity: "error" | "warning" | "info" | "hidden";
  message: string;
  cell?: string;
  startLine: number;
  startColumn: number;
  endLine: number;
  endColumn: number;
}

/** Cell `from` uses the names in `via` from cell `to`. */
export interface Edge {
  from: string;
  to: string;
  via: string[];
}

export interface CellView {
  id: string;
  kind: CellKind;
  source: string;
  attrs: CellAttrs;
  status: CellStatus;
  elapsedMs?: number;
  message?: string;
  outputs: Output[];
  diagnostics: Diagnostic[];
  edited: boolean;
}

export type ServerMessage =
  | {
      type: "notebook.snapshot";
      notebookVersion: number;
      ack: number;
      path: string;
      name: string;
      lazy: boolean;
      cells: CellView[];
      packages: { name: string; version?: string }[];
      kernel: { state: KernelState; message?: string };
      notebookDiagnostics: Diagnostic[];
      edges: Edge[];
    }
  | { type: "cell.status"; cellId: string; status: CellStatus; elapsedMs?: number; message?: string }
  | { type: "cell.outputs"; cellId: string; outputs: Output[] }
  | { type: "cell.stream"; cellId: string; stream: "stdout" | "stderr"; text: string }
  | { type: "cell.diagnostics"; cellId?: string; notebookVersion: number; diagnostics: Diagnostic[] }
  | { type: "graph.update"; notebookVersion: number; edges: Edge[] }
  | { type: "kernel.status"; state: KernelState; message?: string }
  | { type: "file.saved"; notebookVersion: number; ack: number }
  | { type: "file.externalChange"; notebookVersion: number; message: string }
  | { type: "error"; message: string };

export type ClientMessage =
  | { type: "cell.update"; cellId: string; source: string }
  | { type: "cell.run"; cellId: string }
  | { type: "notebook.runAll" }
  | { type: "notebook.setLazy"; lazy: boolean }
  | { type: "notebook.save" }
  | { type: "cell.create"; cellId: string; afterCellId: string | null; kind: "code" | "markdown"; source?: string }
  | { type: "cell.delete"; cellId: string }
  | { type: "cell.move"; cellId: string; afterCellId: string | null }
  | { type: "cell.setAttrs"; cellId: string; attrs: CellAttrs }
  | { type: "widget.setValue"; widgetId: string; value: unknown }
  | { type: "kernel.interrupt" }
  | { type: "kernel.restart" };

export const MIME = {
  plain: "text/plain",
  markdown: "text/markdown",
  html: "text/html",
  svg: "image/svg+xml",
  png: "image/png",
  jpeg: "image/jpeg",
  table: "application/vnd.reverb.table+json",
  object: "application/vnd.reverb.object+json",
  widget: "application/vnd.reverb.widget+json",
  error: "application/vnd.reverb.error+json",
  vegaLite: "application/vnd.vegalite.v5+json",
} as const;
