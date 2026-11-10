import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from "react";
import { flushSync } from "react-dom";
import { CellView, type Dependencies } from "./Cell";
import type { EditorKeys } from "./CodeEditor";
import { PackagesPanel } from "./Packages";
import type { NotebookStore } from "./store";

const isMac = navigator.platform.toUpperCase().includes("MAC");
const mod = (e: KeyboardEvent) => (isMac ? e.metaKey : e.ctrlKey);

export function App({ store }: { store: NotebookStore }) {
  const state = useSyncExternalStore(store.subscribe, store.getState);
  const [selected, setSelected] = useState<string | null>(null);
  const [showPackages, setShowPackages] = useState(false);
  const mode = useRef<"edit" | "command">("command");
  const lastD = useRef(0);

  const cells = state.cells;
  const label = useCallback((id: string) => cells.find((c) => c.id === id)?.attrs.name || id, [cells]);

  const deps = useMemo(() => {
    const map = new Map<string, Dependencies>();
    const get = (id: string) => {
      let d = map.get(id);
      if (!d) map.set(id, (d = { uses: [], usedBy: [] }));
      return d;
    };
    for (const e of state.edges) {
      if (e.to === "types") continue;
      get(e.from).uses.push({ cell: e.to, names: e.via });
      get(e.to).usedBy.push(e.from);
    }
    return map;
  }, [state.edges]);

  const movable = cells.filter((c) => c.kind === "code" || c.kind === "markdown");

  const select = useCallback((id: string, m: "edit" | "command") => {
    setSelected(id);
    mode.current = m;
  }, []);

  const focusCellElement = (id: string) => {
    const el = document.querySelector<HTMLElement>(`[data-cell-id="${CSS.escape(id)}"]`);
    el?.focus({ preventScroll: true });
    el?.scrollIntoView({ block: "nearest" });
  };

  const jump = (id: string) => {
    select(id, "command");
    focusCellElement(id);
  };

  const neighbour = (id: string, step: 1 | -1) => {
    const i = cells.findIndex((c) => c.id === id);
    return cells[i + step]?.id ?? null;
  };

  const runAndNext = (id: string) => {
    const cell = cells.find((c) => c.id === id);
    if (cell && cell.kind !== "markdown") store.run(id);
    const next = neighbour(id, 1);
    // Render and focus before the next key arrives.
    flushSync(() => {
      if (next && cells.find((c) => c.id === next)?.kind !== "types") store.requestFocus(next, "start");
      else store.createCell(id);
    });
  };

  const keys = (id: string): EditorKeys => ({
    run: () => store.run(id),
    runAndNext: () => runAndNext(id),
    runAll: () => store.runAll(),
    save: () => store.save(),
    escape: () => {
      select(id, "command");
      focusCellElement(id);
    },
    up: () => {
      const prev = neighbour(id, -1);
      if (!prev) return false;
      flushSync(() => store.requestFocus(prev, "end"));
      return true;
    },
    down: () => {
      const next = neighbour(id, 1);
      if (!next) return false;
      flushSync(() => store.requestFocus(next, "start"));
      return true;
    },
  });

  // Command mode: single-key shortcuts when no editor has focus.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (mod(e) && e.key.toLowerCase() === "s") {
        e.preventDefault();
        store.save();
        return;
      }
      const target = e.target as HTMLElement;
      if (target.closest(".cm-editor") || target.matches("input, textarea, select")) return;
      if (mod(e) && e.shiftKey && e.key === "Enter") {
        e.preventDefault();
        store.runAll();
        return;
      }
      if (!selected) return;
      if (mod(e) && e.key === "Enter") {
        e.preventDefault();
        store.run(selected);
        return;
      }
      if (e.shiftKey && e.key === "Enter") {
        e.preventDefault();
        runAndNext(selected);
        return;
      }
      if (e.metaKey || e.ctrlKey || e.altKey) return;
      switch (e.key) {
        case "Enter":
          e.preventDefault();
          flushSync(() => store.requestFocus(selected, "end"));
          break;
        case "a":
        case "A": {
          e.preventDefault();
          const i = cells.findIndex((c) => c.id === selected);
          const before = i > 0 ? cells[i - 1] : null;
          flushSync(() => store.createCell(before && before.kind !== "setup" ? before.id : null));
          break;
        }
        case "b":
        case "B":
          e.preventDefault();
          flushSync(() => store.createCell(selected));
          break;
        case "d":
        case "D": {
          const now = Date.now();
          if (now - lastD.current < 600) {
            lastD.current = 0;
            const next = neighbour(selected, 1) ?? neighbour(selected, -1);
            if (cells.find((c) => c.id === selected)?.kind === "setup") break;
            store.deleteCell(selected);
            if (next) jump(next);
          } else {
            lastD.current = now;
          }
          break;
        }
        case "ArrowUp":
        case "k": {
          e.preventDefault();
          const prev = neighbour(selected, -1);
          if (prev) jump(prev);
          break;
        }
        case "ArrowDown":
        case "j": {
          e.preventDefault();
          const next = neighbour(selected, 1);
          if (next) jump(next);
          break;
        }
      }
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  });

  // Unsaved edits are sent before the tab closes; warn if they might not make it.
  useEffect(() => {
    const onUnload = (e: BeforeUnloadEvent) => {
      store.flush();
      if (state.saved === "unsaved" && state.connection !== "open") e.preventDefault();
    };
    window.addEventListener("beforeunload", onUnload);
    return () => window.removeEventListener("beforeunload", onUnload);
  }, [state.saved, state.connection, store]);

  const kernel = state.kernel;
  const lastCodeCell = [...cells].reverse().find((c) => c.kind !== "types");

  return (
    <div className="app">
      <header className="toolbar">
        <span className="brand">reverb</span>
        <span className="file" title={state.path}>
          {state.name}
        </span>
        <span className={`save-state ${state.saved}`} data-testid="save-state">
          {state.saved === "saved" ? "saved" : "editing…"}
        </span>
        <span className="spacer" />
        <span className={`kernel kernel-${kernel.state}`} data-testid="kernel-state" title={kernel.message}>
          <span className="status-dot" aria-hidden="true" />
          kernel {kernel.state}
        </span>
        <button onClick={() => setShowPackages((s) => !s)} aria-expanded={showPackages} title="NuGet packages this notebook uses">
          Packages{state.packages.length > 0 ? ` (${state.packages.length})` : ""}
        </button>
        <label className="lazy" title="Lazy: edits mark dependent cells stale instead of running them">
          <input type="checkbox" checked={state.lazy} onChange={(e) => store.setLazy(e.target.checked)} />
          lazy
        </label>
        <button onClick={() => store.runAll()} title="Run all (Ctrl/Cmd+Shift+Enter)">
          Run all
        </button>
        <button onClick={() => store.interrupt()} disabled={kernel.state !== "busy"} title="Ask the running cell to stop">
          Interrupt
        </button>
        <button onClick={() => store.restart()} title="Stop the kernel and start a new one. Values are lost; outputs stay until cells run again.">
          Restart
        </button>
      </header>

      {state.connection !== "open" && state.loaded && (
        <div className="banner warning" role="status">
          Lost the connection to the Reverb server. Trying to reconnect… (Is <code>reverb edit</code> still running?)
        </div>
      )}
      {showPackages && <PackagesPanel packages={state.packages} store={store} onClose={() => setShowPackages(false)} />}
      {kernel.state === "building" && (
        <div className="banner warning" role="status" data-testid="kernel-building">
          {kernel.message ?? "Getting packages…"} The first time a set of packages is used this takes a little while; after that the kernel is reused.
        </div>
      )}
      {kernel.state === "dead" && kernel.message && (
        <div className="banner error" role="status" data-testid="kernel-dead">
          <pre>{kernel.message}</pre>
        </div>
      )}
      {state.notebookDiagnostics.length > 0 && (
        <div className="banner error" role="status">
          {state.notebookDiagnostics.map((d, i) => (
            <div key={i}>
              {d.id}: {d.message}
            </div>
          ))}
        </div>
      )}

      <main className="cells">
        {!state.loaded && <div className="loading">Connecting…</div>}
        {cells.map((cell) => {
          const m = movable.findIndex((c) => c.id === cell.id);
          return (
            <div key={cell.id} className="cell-slot">
              <CellView
                cell={cell}
                label={label}
                deps={deps.get(cell.id) ?? { uses: [], usedBy: [] }}
                selected={selected === cell.id}
                focus={store.focus}
                store={store}
                canMoveUp={m > 0}
                canMoveDown={m >= 0 && m < movable.length - 1}
                onSelect={select}
                onJump={jump}
                keys={keys}
              />
              {cell.kind !== "types" && (
                <div className="add-between">
                  <button onClick={() => store.createCell(cell.id)} aria-label={`Add code cell after ${label(cell.id)}`}>
                    + code
                  </button>
                  <button onClick={() => store.createCell(cell.id, "markdown")} aria-label={`Add Markdown cell after ${label(cell.id)}`}>
                    + markdown
                  </button>
                </div>
              )}
            </div>
          );
        })}
        {state.loaded && (
          <div className="add-end">
            <button onClick={() => flushSync(() => store.createCell(lastCodeCell?.id ?? null))}>+ Code cell</button>
            <button onClick={() => store.createCell(lastCodeCell?.id ?? null, "markdown")}>+ Markdown cell</button>
          </div>
        )}
      </main>

      <div className="toasts" aria-live="polite">
        {state.toasts.map((t) => (
          <div key={t.id} className={`toast ${t.kind}`} onClick={() => store.dismiss(t.id)}>
            {t.text}
          </div>
        ))}
      </div>
    </div>
  );
}
