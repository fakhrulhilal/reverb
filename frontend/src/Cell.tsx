import { memo, useEffect, useState } from "react";
import { CodeEditor, type EditorKeys } from "./CodeEditor";
import { MarkdownView, Outputs } from "./Outputs";
import type { Cell, FocusRequest, NotebookStore } from "./store";
import type { CellStatus } from "./protocol";

export interface Dependencies {
  /** Cells this one reads from, with the names it reads. */
  uses: { cell: string; names: string[] }[];
  /** Cells that read from this one. */
  usedBy: string[];
}

interface Props {
  cell: Cell;
  label: (id: string) => string;
  deps: Dependencies;
  selected: boolean;
  focus: FocusRequest | null;
  store: NotebookStore;
  canMoveUp: boolean;
  canMoveDown: boolean;
  onSelect: (id: string, mode: "edit" | "command") => void;
  onJump: (id: string) => void;
  keys: (id: string) => EditorKeys;
}

const STATUS_TEXT: Record<CellStatus, string> = {
  idle: "",
  queued: "queued",
  running: "running",
  stale: "stale",
  error: "error",
  "upstream-error": "upstream error",
  disabled: "disabled",
};

function formatMs(ms: number) {
  return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(ms < 10000 ? 1 : 0)} s`;
}

function StatusBadge({ cell }: { cell: Cell }) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (cell.status !== "running") return;
    const t = window.setInterval(() => setNow(Date.now()), 100);
    return () => window.clearInterval(t);
  }, [cell.status]);

  let text = STATUS_TEXT[cell.status];
  if (cell.status === "running" && cell.runningSince) text = `running ${formatMs(Math.max(0, now - cell.runningSince))}`;
  if (cell.status === "idle" && cell.elapsedMs !== undefined) text = formatMs(cell.elapsedMs);
  return (
    <span className={`status status-${cell.status}`} data-testid="cell-status" title={cell.message ?? undefined}>
      <span className="status-dot" aria-hidden="true" />
      {text}
    </span>
  );
}

function CellComponent(props: Props) {
  const { cell, store, deps, label } = props;
  const isMarkdown = cell.kind === "markdown";
  const [editingMarkdown, setEditingMarkdown] = useState(cell.source === "");
  const focus = props.focus?.cellId === cell.id ? props.focus : null;

  useEffect(() => {
    if (focus && isMarkdown) setEditingMarkdown(true);
  }, [focus?.serial]); // eslint-disable-line react-hooks/exhaustive-deps

  const keys = props.keys(cell.id);
  const markdownKeys: EditorKeys = {
    ...keys,
    run: () => {
      if (cell.source.trim() !== "") setEditingMarkdown(false);
      keys.escape();
    },
    runAndNext: () => {
      if (cell.source.trim() !== "") setEditingMarkdown(false);
      keys.runAndNext();
    },
  };

  const showCode = !cell.attrs.hide_code;
  const errors = cell.diagnostics.filter((d) => d.severity === "error" || d.severity === "warning");
  const title = cell.kind === "setup" ? "setup" : cell.kind === "types" ? "types" : label(cell.id);
  const statusMessage = cell.status === "upstream-error" || (cell.status === "error" && !cell.outputs.some((o) => o.data?.["application/vnd.reverb.error+json"]))
    ? cell.message
    : undefined;

  return (
    <section
      className={`cell cell-${cell.kind}${props.selected ? " selected" : ""}${cell.attrs.disabled ? " disabled" : ""}${cell.status === "stale" ? " stale" : ""}`}
      data-cell-id={cell.id}
      data-kind={cell.kind}
      data-status={cell.status}
      aria-label={`Cell ${title}`}
      tabIndex={-1}
      onMouseDown={() => props.onSelect(cell.id, "command")}
    >
      <header className="cell-header">
        {!isMarkdown && cell.kind === "code" && <StatusBadge cell={cell} />}
        <span className="cell-name" title={cell.id}>
          {title}
        </span>
        {cell.edited && <span className="edited" title="Edited since it last ran. Run it (Ctrl/Cmd+Enter) to apply.">edited</span>}
        {(deps.uses.length > 0 || deps.usedBy.length > 0) && (
          <span className="deps">
            {deps.uses.length > 0 && (
              <span>
                uses{" "}
                {deps.uses.map((u, i) => (
                  <span key={u.cell}>
                    {i > 0 && ", "}
                    <button className="link" onClick={() => props.onJump(u.cell)} title={`From cell ${label(u.cell)}`}>
                      {u.names.join(", ")}
                    </button>
                  </span>
                ))}
              </span>
            )}
            {deps.uses.length > 0 && deps.usedBy.length > 0 && <span className="sep"> · </span>}
            {deps.usedBy.length > 0 && (
              <span>
                used by{" "}
                {deps.usedBy.map((c, i) => (
                  <span key={c}>
                    {i > 0 && ", "}
                    <button className="link" onClick={() => props.onJump(c)}>
                      {label(c)}
                    </button>
                  </span>
                ))}
              </span>
            )}
          </span>
        )}
        <span className="cell-actions">
          {cell.kind !== "markdown" && (
            <button className="icon" aria-label="Run cell" title="Run (Ctrl/Cmd+Enter)" onClick={() => store.run(cell.id)}>
              ▶
            </button>
          )}
          {cell.kind === "code" && (
            <button className="icon" aria-label={showCode ? "Hide code" : "Show code"} title={showCode ? "Hide code" : "Show code"} onClick={() => store.setAttrs(cell.id, { hide_code: showCode })}>
              {showCode ? "⌃" : "⌄"}
            </button>
          )}
          {cell.kind === "code" && (
            <button className="icon" aria-label={cell.attrs.disabled ? "Enable cell" : "Disable cell"} title={cell.attrs.disabled ? "Enable" : "Disable"} onClick={() => store.setAttrs(cell.id, { disabled: !cell.attrs.disabled })}>
              {cell.attrs.disabled ? "◉" : "○"}
            </button>
          )}
          {(cell.kind === "code" || isMarkdown) && (
            <>
              <button className="icon" aria-label="Move up" title="Move up" disabled={!props.canMoveUp} onClick={() => store.moveCell(cell.id, -1)}>
                ↑
              </button>
              <button className="icon" aria-label="Move down" title="Move down" disabled={!props.canMoveDown} onClick={() => store.moveCell(cell.id, 1)}>
                ↓
              </button>
            </>
          )}
          {cell.kind !== "setup" && (
            <button className="icon danger" aria-label="Delete cell" title="Delete (D D)" onClick={() => store.deleteCell(cell.id)}>
              ×
            </button>
          )}
        </span>
      </header>

      {cell.outputs.length > 0 && <Outputs outputs={cell.outputs} />}
      {statusMessage && <div className="status-message">{statusMessage}</div>}

      {isMarkdown && !editingMarkdown ? (
        <div className="markdown-cell" onDoubleClick={() => { setEditingMarkdown(true); store.requestFocus(cell.id); }} title="Double-click to edit">
          <MarkdownView text={cell.source} />
        </div>
      ) : showCode || isMarkdown ? (
        <CodeEditor
          value={cell.source}
          language={isMarkdown ? "markdown" : "csharp"}
          diagnostics={cell.diagnostics}
          onChange={(v) => store.edit(cell.id, v)}
          onFocus={() => props.onSelect(cell.id, "edit")}
          keys={isMarkdown ? markdownKeys : keys}
          focus={focus}
          label={`Code for cell ${title}`}
          placeholder={isMarkdown ? "Markdown" : cell.kind === "setup" ? "using directives" : "C# code"}
        />
      ) : (
        <button className="hidden-code" onClick={() => store.setAttrs(cell.id, { hide_code: false })}>
          code hidden · show
        </button>
      )}

      {errors.length > 0 && (showCode || isMarkdown) && (
        <ul className="diagnostics">
          {errors.map((d, i) => (
            <li key={i} className={d.severity}>
              <span className="where">line {d.startLine + 1}</span> {d.id}: {d.message}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

export const CellView = memo(CellComponent);
