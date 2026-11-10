import type { CellAttrs, CellView, ClientMessage, Diagnostic, Edge, KernelState, LangRequest, Output, Package, ServerMessage, WidgetKind } from "./protocol";

type LangResult = Extract<ServerMessage, { requestId: number }>;

export interface Cell extends CellView {
  /** When the cell started running, for the elapsed-time badge (local clock). */
  runningSince?: number;
}

export interface Toast {
  id: number;
  kind: "info" | "error";
  text: string;
}

export interface NotebookState {
  connection: "connecting" | "open" | "closed";
  loaded: boolean;
  mode: "edit" | "app";
  name: string;
  path: string;
  lazy: boolean;
  kernel: { state: KernelState; message?: string };
  cells: Cell[];
  edges: Edge[];
  packages: Package[];
  notebookDiagnostics: Diagnostic[];
  saved: "saved" | "unsaved";
  toasts: Toast[];
}

/** A request for an editor to take focus (consumed by the editor it names). */
export interface FocusRequest {
  cellId: string;
  at: "start" | "end";
  serial: number;
}

const UPDATE_DELAY_MS = 300;

type Listener = () => void;

/**
 * The notebook as this tab sees it, plus the WebSocket. The server is the source of truth, but
 * typing shows up locally at once: edits are sent after a short pause and re-applied on top of
 * any snapshot that arrives before the server has handled them.
 */
export class NotebookStore {
  state: NotebookState = {
    connection: "connecting",
    loaded: false,
    mode: "edit",
    name: "",
    path: "",
    lazy: false,
    kernel: { state: "starting" },
    cells: [],
    edges: [],
    packages: [],
    notebookDiagnostics: [],
    saved: "saved",
    toasts: [],
  };
  focus: FocusRequest | null = null;

  private listeners = new Set<Listener>();
  private socket: WebSocket | null = null;
  private seq = 0;
  /** Local edits the server hasn't confirmed: the source, and the seq it was sent with (0 = not sent yet). */
  // `ran`: the edit was run too, so the cell isn't "edited" even if a snapshot from before the run says so.
  private pending = new Map<string, { source: string; seq: number; ran?: boolean }>();
  private timers = new Map<string, number>();
  /** The seq of the last add/delete/move this tab sent. Snapshots older than it don't know about it yet. */
  private structuralSeq = 0;
  private toastId = 0;
  private retryMs = 250;
  private closedByUs = false;

  constructor(private readonly url: string) {}

  // ---- subscription (for useSyncExternalStore) ----------------------------------------------

  subscribe = (listener: Listener) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  getState = () => this.state;

  private set(patch: Partial<NotebookState>) {
    this.state = { ...this.state, ...patch };
    for (const l of this.listeners) l();
  }

  private updateCell(id: string, change: (cell: Cell) => Cell) {
    const cells = this.state.cells.map((c) => (c.id === id ? change(c) : c));
    this.set({ cells });
  }

  // ---- connection ---------------------------------------------------------------------------

  connect() {
    this.closedByUs = false;
    const socket = new WebSocket(this.url);
    this.socket = socket;
    this.set({ connection: "connecting" });
    socket.onopen = () => {
      this.retryMs = 250;
      this.set({ connection: "open" });
    };
    socket.onmessage = (e) => this.receive(JSON.parse(e.data as string) as ServerMessage);
    socket.onclose = () => {
      if (this.socket !== socket) return;
      this.socket = null;
      this.set({ connection: "closed" });
      if (this.closedByUs) return;
      // Reconnect with backoff; the server sends a fresh snapshot when we're back.
      window.setTimeout(() => this.connect(), this.retryMs);
      this.retryMs = Math.min(this.retryMs * 2, 5000);
    };
  }

  close() {
    this.closedByUs = true;
    this.socket?.close();
  }

  private nextRequest = 0;
  private answers = new Map<number, (m: LangResult | null) => void>();

  /**
   * Asks the kernel about the code in a cell: completions, hover or signature help. Sends the
   * cell's text as this tab has it, so the answer fits what's on screen. Resolves to null when
   * there's no answer (no kernel, or it took too long).
   */
  ask(type: LangRequest, cellId: string, source: string, offset: number): Promise<LangResult | null> {
    const requestId = ++this.nextRequest;
    return new Promise((resolve) => {
      const timer = setTimeout(() => {
        this.answers.delete(requestId);
        resolve(null);
      }, 10_000);
      this.answers.set(requestId, (m) => {
        clearTimeout(timer);
        resolve(m);
      });
      this.send({ type, requestId, cellId, source, offset });
    });
  }

  /** Applies Reverb's fix for a diagnostic. The notebook updates when the server has made it. */
  codeFix(cellId: string, d: Diagnostic) {
    this.flush();
    this.send({ type: "lang.codeFix", cellId, diagnosticId: d.id, line: d.startLine, column: d.startColumn });
  }

  private send(message: ClientMessage): number {
    const seq = ++this.seq;
    if (this.socket?.readyState === WebSocket.OPEN) this.socket.send(JSON.stringify({ ...message, seq }));
    return seq;
  }

  private receive(m: ServerMessage) {
    switch (m.type) {
      case "notebook.snapshot":
        this.applySnapshot(m);
        break;
      case "cell.status":
        this.updateCell(m.cellId, (c) => ({
          ...c,
          status: m.status,
          elapsedMs: m.elapsedMs,
          message: m.message,
          runs: m.runs ?? c.runs,
          runningSince: m.status === "running" ? Date.now() : undefined,
        }));
        break;
      case "cell.outputs":
        this.updateCell(m.cellId, (c) => ({ ...c, outputs: m.outputs }));
        break;
      case "cell.stream":
        this.updateCell(m.cellId, (c) => ({ ...c, outputs: appendStream(c.outputs, m.stream, m.text) }));
        break;
      case "cell.diagnostics":
        if (m.cellId === undefined) this.set({ notebookDiagnostics: m.diagnostics });
        else this.updateCell(m.cellId, (c) => ({ ...c, diagnostics: m.diagnostics }));
        break;
      case "graph.update":
        this.set({ edges: m.edges });
        break;
      case "kernel.status":
        this.set({ kernel: { state: m.state, message: m.message } });
        if (m.state === "dead" && m.message) this.toast("error", m.message);
        break;
      case "file.saved":
        for (const [id, p] of this.pending) if (p.seq !== 0 && p.seq <= m.ack) this.pending.delete(id);
        if (this.pending.size === 0) this.set({ saved: "saved" });
        break;
      case "file.externalChange":
        this.toast("info", m.message);
        break;
      case "error":
        this.toast("error", m.message);
        break;
      case "lang.completionsResult":
      case "lang.hoverResult":
      case "lang.signatureHelpResult": {
        const answer = this.answers.get(m.requestId);
        this.answers.delete(m.requestId);
        answer?.(m);
        break;
      }
    }
  }

  private applySnapshot(m: Extract<ServerMessage, { type: "notebook.snapshot" }>) {
    for (const [id, p] of this.pending) if (p.seq !== 0 && p.seq <= m.ack) this.pending.delete(id);
    const previous = new Map(this.state.cells.map((c) => [c.id, c]));
    // The server hasn't seen our latest add/delete/move yet: keep our cell list and take only
    // what the kernel reported for the cells we both have. A later snapshot will catch up.
    const layout = m.ack < this.structuralSeq
      ? this.state.cells.map((local) => m.cells.find((c) => c.id === local.id) ?? local)
      : m.cells;
    const cells: Cell[] = layout.map((c) => {
      const local = this.pending.get(c.id);
      const old = previous.get(c.id);
      const source = local ? local.source : c.source;
      return {
        ...c,
        source,
        edited: local && isCode(c.kind) ? !local.ran : c.edited,
        runningSince: c.status === "running" ? (old?.runningSince ?? Date.now()) : undefined,
      };
    });
    if (m.ack >= this.structuralSeq)
      for (const id of this.pending.keys()) if (!cells.some((c) => c.id === id)) this.pending.delete(id);
    // Edits sent on a connection that dropped never arrived: send them again.
    for (const [id, p] of this.pending) if (p.seq !== 0 && p.seq > m.ack) this.flushCell(id);
    this.set({
      loaded: true,
      mode: m.mode,
      name: m.name,
      path: m.path,
      lazy: m.lazy,
      kernel: m.kernel,
      cells,
      edges: m.edges,
      packages: m.packages,
      notebookDiagnostics: m.notebookDiagnostics,
    });
    document.title = `${m.name} · Reverb`;
  }

  // ---- toasts -------------------------------------------------------------------------------

  toast(kind: Toast["kind"], text: string) {
    const id = ++this.toastId;
    this.set({ toasts: [...this.state.toasts, { id, kind, text }] });
    window.setTimeout(() => this.dismiss(id), kind === "error" ? 10000 : 5000);
  }

  dismiss(id: number) {
    this.set({ toasts: this.state.toasts.filter((t) => t.id !== id) });
  }

  // ---- editing ------------------------------------------------------------------------------

  /** The user typed in a cell. Shown at once; sent after a pause. */
  edit(cellId: string, source: string) {
    const cell = this.state.cells.find((c) => c.id === cellId);
    if (!cell || cell.source === source) return;
    this.pending.set(cellId, { source, seq: 0 });
    this.updateCell(cellId, (c) => ({ ...c, source, edited: isCode(c.kind) ? true : c.edited }));
    this.set({ saved: "unsaved" });
    window.clearTimeout(this.timers.get(cellId));
    this.timers.set(cellId, window.setTimeout(() => this.flushCell(cellId), UPDATE_DELAY_MS));
  }

  private flushCell(cellId: string) {
    window.clearTimeout(this.timers.get(cellId));
    this.timers.delete(cellId);
    const p = this.pending.get(cellId);
    if (!p) return;
    p.seq = this.send({ type: "cell.update", cellId, source: p.source });
  }

  /** Sends edits still waiting for their pause, so the next message sees them. */
  flush() {
    for (const [id, p] of this.pending) if (p.seq === 0) this.flushCell(id);
  }

  run(cellId: string) {
    this.flush();
    const p = this.pending.get(cellId);
    if (p) p.ran = true;
    this.send({ type: "cell.run", cellId });
    this.updateCell(cellId, (c) => ({ ...c, edited: false }));
  }

  runAll() {
    this.flush();
    for (const p of this.pending.values()) p.ran = true;
    this.send({ type: "notebook.runAll" });
    this.set({ cells: this.state.cells.map((c) => ({ ...c, edited: false })) });
  }

  save() {
    this.flush();
    this.send({ type: "notebook.save" });
  }

  setLazy(lazy: boolean) {
    this.flush();
    this.send({ type: "notebook.setLazy", lazy });
    this.set({ lazy });
  }

  interrupt() {
    this.send({ type: "kernel.interrupt" });
  }

  restart() {
    this.send({ type: "kernel.restart" });
    this.set({ kernel: { state: "starting" } });
  }

  /** Replaces the notebook's packages. The server rewrites the #:package lines and rebuilds the kernel. */
  setPackages(packages: Package[]) {
    this.flush();
    this.send({ type: "packages.set", packages });
    this.set({ packages });
  }

  /** Adds a cell after `afterCellId` (null: at the top) and focuses it. Returns its id. */
  createCell(afterCellId: string | null, kind: "code" | "markdown" = "code"): string {
    this.flush();
    const id = newCellId(new Set(this.state.cells.map((c) => c.id)));
    this.structuralSeq = this.send({ type: "cell.create", cellId: id, afterCellId, kind });
    // Show it now; the snapshot that follows confirms where it went.
    const cell: Cell = { id, kind, source: "", attrs: {}, status: "idle", outputs: [], diagnostics: [], edited: false, runs: 0 };
    const cells = [...this.state.cells];
    const at = afterCellId === null ? firstSlot(cells) : cells.findIndex((c) => c.id === afterCellId) + 1;
    cells.splice(Math.min(Math.max(at, firstSlot(cells)), lastSlot(cells)), 0, cell);
    this.set({ cells });
    this.requestFocus(id, "start");
    return id;
  }

  deleteCell(cellId: string) {
    this.flush();
    this.pending.delete(cellId);
    this.structuralSeq = this.send({ type: "cell.delete", cellId });
    this.set({ cells: this.state.cells.filter((c) => c.id !== cellId) });
  }

  /** Moves a cell one place up (-1) or down (+1) among the code and Markdown cells. */
  moveCell(cellId: string, direction: -1 | 1) {
    const movable = this.state.cells.filter((c) => c.kind === "code" || c.kind === "markdown");
    const i = movable.findIndex((c) => c.id === cellId);
    const j = i + direction;
    if (i < 0 || j < 0 || j >= movable.length) return;
    this.flush();
    const order = [...movable];
    order.splice(i, 1);
    order.splice(j, 0, movable[i]);
    const afterCellId = j === 0 ? null : order[j - 1].id;
    this.structuralSeq = this.send({ type: "cell.move", cellId, afterCellId });
    const others = this.state.cells.filter((c) => !(c.kind === "code" || c.kind === "markdown"));
    const setup = others.filter((c) => c.kind === "setup");
    const types = others.filter((c) => c.kind === "types");
    this.set({ cells: [...setup, ...order, ...types] });
  }

  setAttrs(cellId: string, attrs: CellAttrs) {
    this.flush();
    this.send({ type: "cell.setAttrs", cellId, attrs });
    this.updateCell(cellId, (c) => ({ ...c, attrs: { ...c.attrs, ...attrs } }));
  }

  setWidget = (widgetId: string, value: unknown, kind: WidgetKind) => {
    this.send({ type: "widget.setValue", widgetId, value, kind });
  };

  requestFocus(cellId: string, at: "start" | "end" = "end") {
    this.focus = { cellId, at, serial: (this.focus?.serial ?? 0) + 1 };
    this.set({});
  }
}

export function isCode(kind: Cell["kind"]) {
  return kind === "code" || kind === "setup" || kind === "types";
}

function firstSlot(cells: Cell[]) {
  return cells.length > 0 && cells[0].kind === "setup" ? 1 : 0;
}

function lastSlot(cells: Cell[]) {
  return cells.length > 0 && cells[cells.length - 1].kind === "types" ? cells.length - 1 : cells.length;
}

function appendStream(outputs: Output[], stream: "stdout" | "stderr", text: string): Output[] {
  const last = outputs[outputs.length - 1];
  if (last && last.stream === stream) return [...outputs.slice(0, -1), { ...last, text: (last.text ?? "") + text }];
  return [...outputs, { stream, text }];
}

/** Six characters, a letter first, like the ids Reverb writes into files. */
function newCellId(taken: Set<string>): string {
  const letters = "abcdefghijklmnopqrstuvwxyz";
  const alphabet = letters + "0123456789";
  for (;;) {
    const bytes = crypto.getRandomValues(new Uint8Array(6));
    let id = letters[bytes[0] % letters.length];
    for (let i = 1; i < 6; i++) id += alphabet[bytes[i] % alphabet.length];
    if (!taken.has(id) && id !== "setup" && id !== "types") return id;
  }
}
