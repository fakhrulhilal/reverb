import { MarkdownView, Outputs } from "../Outputs";
import type { CellKind, Output } from "../protocol";
import { ThemeButton } from "../ThemeButton";
import { HighlightedCode } from "./highlight";

export interface ExportedCell {
  id: string;
  kind: CellKind;
  /** Missing when code isn't part of the export. */
  source?: string;
  status: string;
  message?: string;
  outputs: Output[];
}

export interface ExportedNotebook {
  name: string;
  reverbVersion: string;
  cells: ExportedCell[];
}

/** A static page of a notebook's outputs, as `reverb export --html` writes it. */
export function ExportView({ notebook }: { notebook: ExportedNotebook }) {
  return (
    <div className="app export">
      <header className="export-header">
        <span className="file">{notebook.name}</span>
        <span className="spacer" />
        <ThemeButton />
      </header>
      <main className="cells">
        {notebook.cells.map((cell) => {
          if (cell.kind === "markdown") {
            if (!cell.source?.trim()) return null;
            return (
              <section key={cell.id} className="export-cell export-markdown">
                <MarkdownView text={cell.source} />
              </section>
            );
          }
          const failed = cell.status === "error" || cell.status === "upstream-error";
          const showMessage = failed && cell.message && !cell.outputs.some((o) => o.data?.["application/vnd.reverb.error+json"]);
          const hasCode = cell.source !== undefined && cell.source.trim() !== "";
          if (cell.outputs.length === 0 && !showMessage && !hasCode) return null;
          return (
            <section key={cell.id} className="export-cell" data-cell-id={cell.id} data-status={cell.status}>
              {cell.outputs.length > 0 && <Outputs outputs={cell.outputs} />}
              {showMessage && <div className="status-message">{cell.message}</div>}
              {hasCode && (
                <details className="export-source" open={cell.outputs.length === 0 && cell.kind === "code"}>
                  <summary>{cell.kind === "setup" ? "Setup" : cell.kind === "types" ? "Types" : "Code"}</summary>
                  <HighlightedCode code={cell.source!} />
                </details>
              )}
            </section>
          );
        })}
      </main>
      <footer className="export-footer">
        Exported from <code>{notebook.name}</code> with Reverb {notebook.reverbVersion}. The notebook is a plain C# file: run it with <code>dotnet run {notebook.name}</code>.
      </footer>
    </div>
  );
}
