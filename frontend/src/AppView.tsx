import { useSyncExternalStore } from "react";
import { MarkdownView, Outputs } from "./Outputs";
import type { NotebookStore } from "./store";
import { ThemeButton } from "./ThemeButton";

/**
 * A notebook served with `reverb run`: Markdown and outputs, widgets live, no code and no editing.
 * Cells that show nothing are left out.
 */
export function AppView({ store }: { store: NotebookStore }) {
  const state = useSyncExternalStore(store.subscribe, store.getState);
  const busy = state.kernel.state === "busy" || state.cells.some((c) => c.status === "running" || c.status === "queued");

  return (
    <div className="app app-mode">
      <div className={`app-progress${busy ? " active" : ""}`} aria-hidden="true" />
      {state.connection !== "open" && state.loaded && (
        <div className="banner warning" role="status">
          Lost the connection to the server. Trying to reconnect…
        </div>
      )}
      {state.kernel.state === "dead" && state.kernel.message && (
        <div className="banner error" role="status">
          {state.kernel.message}
        </div>
      )}
      <ThemeButton className="app-theme" />
      <main className="cells app-cells">
        {!state.loaded && <div className="loading">Loading…</div>}
        {state.cells.map((cell) => {
          if (cell.kind === "markdown") {
            if (cell.source.trim() === "") return null;
            return (
              <section key={cell.id} className="app-cell app-markdown" data-cell-id={cell.id}>
                <MarkdownView text={cell.source} />
              </section>
            );
          }
          const failed = cell.status === "error" || cell.status === "upstream-error";
          const showMessage = failed && !cell.outputs.some((o) => o.data?.["application/vnd.reverb.error+json"]);
          if (cell.outputs.length === 0 && !showMessage) return null;
          return (
            <section
              key={cell.id}
              className={`app-cell${cell.status === "stale" || cell.status === "running" || cell.status === "queued" ? " updating" : ""}`}
              data-cell-id={cell.id}
              data-status={cell.status}
              data-runs={cell.runs}
            >
              {cell.outputs.length > 0 && <Outputs outputs={cell.outputs} />}
              {showMessage && <div className="status-message">This part of the app couldn't be shown: {cell.message}</div>}
            </section>
          );
        })}
      </main>
    </div>
  );
}
