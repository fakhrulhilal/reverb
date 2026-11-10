// Completions, hover and signature help in a cell editor, answered by the kernel.
import { autocompletion, type Completion as CmCompletion, type CompletionContext, type CompletionResult } from "@codemirror/autocomplete";
import { StateEffect, StateField, type Extension } from "@codemirror/state";
import { EditorView, hoverTooltip, showTooltip, type Tooltip } from "@codemirror/view";
import { renderMarkdown } from "./markdown";
import type { Completion, LangRequest, ServerMessage } from "./protocol";

type Answer = Extract<ServerMessage, { requestId: number }>;

/** Sends a language request for this cell. Returns null when there's no answer. */
export type Ask = (type: LangRequest, source: string, offset: number) => Promise<Answer | null>;

// CodeMirror's completion icons know these names.
const kinds: Record<string, string> = {
  variable: "variable",
  method: "method",
  property: "property",
  field: "property",
  event: "property",
  class: "class",
  struct: "class",
  interface: "interface",
  enum: "enum",
  "enum-member": "enum",
  namespace: "namespace",
  keyword: "keyword",
  type: "type",
};

function option(c: Completion): CmCompletion {
  return {
    label: c.label,
    apply: c.insert ?? undefined,
    type: kinds[c.kind] ?? "text",
    detail: c.detail ?? undefined,
    // The notebook's own variables first when the match is equally good.
    boost: c.kind === "variable" ? 2 : 0,
  };
}

/** Inside a string or a // comment on this line: no completions there. A rough check; verbatim and raw strings fool it. */
function inStringOrComment(context: CompletionContext): boolean {
  const line = context.state.doc.lineAt(context.pos);
  const before = line.text.slice(0, context.pos - line.from);
  let quoted = false;
  for (let i = 0; i < before.length; i++) {
    const c = before[i];
    if (c === "\\" && quoted) i++;
    else if (c === '"') quoted = !quoted;
    else if (!quoted && c === "/" && before[i + 1] === "/") return true;
  }
  return quoted;
}

function completions(ask: () => Ask | undefined) {
  return async (context: CompletionContext): Promise<CompletionResult | null> => {
    const request = ask();
    if (!request || inStringOrComment(context)) return null;
    const word = context.matchBefore(/\w*/);
    const before = context.state.sliceDoc(context.pos - 1, context.pos);
    // Ask when the user is typing a name or just typed a dot (or pressed Ctrl+Space).
    if (!context.explicit && (!word || word.from === word.to) && before !== ".") return null;
    const answer = await request("lang.completions", context.state.doc.toString(), context.pos);
    if (context.aborted || !answer || answer.type !== "lang.completionsResult" || !answer.completions) return null;
    return {
      from: answer.from ?? word?.from ?? context.pos,
      options: answer.completions.map(option),
      // Keep filtering locally while the user types more of the same name.
      validFor: /^\w*$/,
    };
  };
}

function hover(ask: () => Ask | undefined) {
  return hoverTooltip(async (view, pos): Promise<Tooltip | null> => {
    const request = ask();
    if (!request) return null;
    const answer = await request("lang.hover", view.state.doc.toString(), pos);
    if (!answer || answer.type !== "lang.hoverResult" || !answer.text) return null;
    const text = answer.text;
    return {
      pos: answer.from ?? pos,
      end: answer.to ?? pos,
      above: true,
      create: () => {
        const dom = document.createElement("div");
        dom.className = "lang-hover";
        dom.innerHTML = renderMarkdown(text);
        return { dom };
      },
    };
  }, { hoverTime: 350 });
}

// ---- signature help ---------------------------------------------------------------------------

/** The tooltip, and where the '(' it belongs to is. */
interface SignatureState {
  tooltip: Tooltip;
  open: number;
}

const setSignature = StateEffect.define<SignatureState | null>();

/** The unmatched '(' before `pos`, or -1. Strings and comments aren't skipped; it's a hint, not a parser. */
function openParen(text: string, pos: number): number {
  let depth = 0;
  for (let i = pos - 1; i >= 0; i--) {
    const c = text[i];
    if (c === ")") depth++;
    else if (c === "(") {
      if (depth === 0) return i;
      depth--;
    } else if (c === ";" || c === "{" || c === "}") return -1;
  }
  return -1;
}

/** Whether the caret is still between the '(' at `open` and its ')'. */
function stillInside(text: string, open: number, caret: number): boolean {
  if (caret <= open || text[open] !== "(") return false;
  let depth = 1;
  for (let i = open + 1; i < caret; i++) {
    if (text[i] === "(") depth++;
    else if (text[i] === ")" && --depth === 0) return false;
  }
  return true;
}

const signatureField = StateField.define<SignatureState | null>({
  create: () => null,
  update(value, tr) {
    for (const e of tr.effects) if (e.is(setSignature)) return e.value;
    if (!value) return null;
    const open = tr.changes.mapPos(value.open);
    // Gone as soon as the caret leaves the call, without waiting for the kernel to say so.
    if (!stillInside(tr.state.doc.toString(), open, tr.state.selection.main.head)) return null;
    return tr.docChanged ? { open, tooltip: { ...value.tooltip, pos: tr.changes.mapPos(value.tooltip.pos) } } : value;
  },
  provide: (f) => showTooltip.compute([f], (state) => state.field(f)?.tooltip ?? null),
});

function signatureTooltip(answer: Extract<Answer, { type: "lang.signatureHelpResult" }>, pos: number): Tooltip | null {
  const signatures = answer.signatures ?? [];
  if (signatures.length === 0) return null;
  const index = Math.min(Math.max(answer.activeSignature ?? 0, 0), signatures.length - 1);
  const signature = signatures[index];
  const active = answer.activeParameter ?? 0;
  return {
    pos,
    above: true,
    strictSide: false,
    create: () => {
      const dom = document.createElement("div");
      dom.className = "lang-signature";
      dom.setAttribute("data-testid", "signature-help");
      // The signature, with the parameter being typed in bold.
      const label = signature.label;
      const parameter = signature.parameters[active];
      const at = parameter ? label.indexOf(parameter, label.indexOf("(")) : -1;
      const code = document.createElement("code");
      if (at >= 0) {
        const strong = document.createElement("strong");
        strong.textContent = parameter;
        code.append(label.slice(0, at), strong, label.slice(at + parameter.length));
      } else {
        code.textContent = label;
      }
      dom.append(code);
      if (signatures.length > 1) {
        const count = document.createElement("span");
        count.className = "count";
        count.textContent = ` ${index + 1} of ${signatures.length}`;
        dom.append(count);
      }
      return { dom };
    },
  };
}

function signatureHelp(ask: () => Ask | undefined): Extension {
  let serial = 0;
  let waiting = false;
  return [
    signatureField,
    EditorView.updateListener.of((u) => {
      const open = u.state.field(signatureField) !== null;
      const typedOpener = u.transactions.some((tr) => {
        if (!tr.isUserEvent("input")) return false;
        let found = false;
        tr.changes.iterChanges((_a, _b, _c, _d, inserted) => {
          if (/[(,]/.test(inserted.toString())) found = true;
        });
        return found;
      });
      // Ask on '(' or ','; then keep asking as the user types or moves, while it shows or an answer is due.
      if (!typedOpener && !((open || waiting) && (u.docChanged || u.selectionSet))) return;
      const request = ask();
      if (!request) return;
      const pos = u.state.selection.main.head;
      const text = u.state.doc.toString();
      const paren = openParen(text, pos);
      if (paren < 0) {
        waiting = false;
        serial++;
        if (open) u.view.dispatch({ effects: setSignature.of(null) });
        return;
      }
      const mine = ++serial;
      waiting = true;
      void request("lang.signatureHelp", text, pos).then((answer) => {
        if (mine !== serial) return; // a newer question is out
        waiting = false;
        const now = u.view.state;
        if (now.doc.toString() !== text || now.selection.main.head !== pos) return;
        const tooltip = answer && answer.type === "lang.signatureHelpResult" ? signatureTooltip(answer, pos) : null;
        if (tooltip || now.field(signatureField) !== null)
          u.view.dispatch({ effects: setSignature.of(tooltip ? { tooltip, open: paren } : null) });
      });
    }),
  ];
}

/** Closes signature help if it's showing. Returns whether it was. */
export function closeSignatureHelp(view: EditorView): boolean {
  if (view.state.field(signatureField, false) == null) return false;
  view.dispatch({ effects: setSignature.of(null) });
  return true;
}

/** Everything a C# cell editor gets from the kernel. `ask` is read each time, so it can change. */
export function languageServices(ask: () => Ask | undefined): Extension {
  return [autocompletion({ override: [completions(ask)], icons: true }), hover(ask), signatureHelp(ask)];
}
