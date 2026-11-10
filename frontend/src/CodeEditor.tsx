import { useEffect, useLayoutEffect, useRef } from "react";
import { EditorState, Compartment, Prec, type Extension } from "@codemirror/state";
import { EditorView, keymap, lineNumbers, drawSelection, highlightActiveLine, placeholder as placeholderExt } from "@codemirror/view";
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import { StreamLanguage, bracketMatching, indentOnInput, syntaxHighlighting, HighlightStyle } from "@codemirror/language";
import { closeBrackets, closeBracketsKeymap, closeCompletion, completionStatus, currentCompletions } from "@codemirror/autocomplete";
import { highlightSelectionMatches, searchKeymap } from "@codemirror/search";
import { lintGutter, setDiagnostics, type Diagnostic as CmDiagnostic } from "@codemirror/lint";
import { csharp } from "@codemirror/legacy-modes/mode/clike";
import { tags } from "@lezer/highlight";
import { closeSignatureHelp, languageServices, type Ask } from "./language";
import type { Diagnostic } from "./protocol";

export interface EditorKeys {
  run: () => void;
  runAndNext: () => void;
  runAll: () => void;
  save: () => void;
  escape: () => void;
  /** Arrow up on the first line: move to the cell above. Return false to let the editor handle it. */
  up: () => boolean;
  down: () => boolean;
}

interface Props {
  value: string;
  language: "csharp" | "markdown";
  diagnostics: Diagnostic[];
  onChange: (value: string) => void;
  onFocus: () => void;
  keys: EditorKeys;
  focus?: { at: "start" | "end"; serial: number } | null;
  placeholder?: string;
  label: string;
  /** Completions, hover and signature help for C# cells. */
  ask?: Ask;
}

// Colours come from CSS variables, so the editor follows the light and dark themes.
const highlight = HighlightStyle.define([
  { tag: tags.keyword, color: "var(--syntax-keyword)" },
  { tag: [tags.string, tags.special(tags.string)], color: "var(--syntax-string)" },
  { tag: [tags.number, tags.bool, tags.null, tags.atom], color: "var(--syntax-number)" },
  { tag: tags.comment, color: "var(--syntax-comment)", fontStyle: "italic" },
  { tag: [tags.typeName, tags.className], color: "var(--syntax-type)" },
  { tag: [tags.definition(tags.variableName), tags.function(tags.variableName)], color: "var(--syntax-def)" },
  { tag: tags.meta, color: "var(--syntax-meta)" },
  { tag: tags.heading, fontWeight: "600" },
  { tag: tags.emphasis, fontStyle: "italic" },
  { tag: tags.strong, fontWeight: "600" },
  { tag: tags.link, color: "var(--syntax-string)" },
]);

const theme = EditorView.theme({
  "&": { fontSize: "13.5px", backgroundColor: "transparent" },
  "&.cm-focused": { outline: "none" },
  ".cm-content": { fontFamily: "var(--font-mono)", padding: "8px 0", caretColor: "var(--text)" },
  ".cm-gutters": { backgroundColor: "transparent", border: "none", color: "var(--text-faint)" },
  ".cm-lineNumbers .cm-gutterElement": { padding: "0 8px 0 12px", minWidth: "28px" },
  ".cm-activeLine": { backgroundColor: "var(--active-line)" },
  ".cm-cursor": { borderLeftColor: "var(--text)" },
  "&.cm-focused .cm-selectionBackground, .cm-selectionBackground, ::selection": { backgroundColor: "var(--selection) !important" },
  ".cm-tooltip": { backgroundColor: "var(--surface)", color: "var(--text)", border: "1px solid var(--border)" },
  ".cm-placeholder": { color: "var(--text-faint)" },
});

/** Turns cell-relative line/column ranges into document offsets, clamped to the current text. */
function toCm(state: EditorState, list: Diagnostic[]): CmDiagnostic[] {
  const doc = state.doc;
  const offset = (line: number, column: number) => {
    if (line >= doc.lines) return doc.length;
    const l = doc.line(Math.max(0, line) + 1);
    return Math.min(l.from + Math.max(0, column), l.to);
  };
  return list
    .filter((d) => d.severity === "error" || d.severity === "warning")
    .map((d) => {
      const from = offset(d.startLine, d.startColumn);
      const to = Math.max(from, offset(d.endLine, d.endColumn));
      return { from, to, severity: d.severity as "error" | "warning", message: `${d.id}: ${d.message}` };
    });
}

export function CodeEditor(props: Props) {
  const host = useRef<HTMLDivElement>(null);
  const view = useRef<EditorView | null>(null);
  // The latest props, for callbacks the editor was created with.
  const latest = useRef(props);
  latest.current = props;
  const languageSlot = useRef(new Compartment());

  useLayoutEffect(() => {
    const k = () => latest.current.keys;
    const extensions: Extension[] = [
      lineNumbers(),
      history(),
      drawSelection(),
      highlightActiveLine(),
      bracketMatching(),
      closeBrackets(),
      indentOnInput(),
      highlightSelectionMatches(),
      lintGutter(),
      syntaxHighlighting(highlight),
      theme,
      EditorView.lineWrapping,
      EditorView.contentAttributes.of({ "aria-label": props.label }),
      placeholderExt(props.placeholder ?? ""),
      languageSlot.current.of(props.language === "csharp" ? StreamLanguage.define(csharp) : []),
      // Escape closes what's showing (signature help, then the completion list) and otherwise
      // leaves the editor. Completion state with nothing on screen (a request still on its way,
      // or an empty list) mustn't swallow it. Listed before the completion extension, whose own
      // Escape binding has the same precedence and would otherwise run first.
      Prec.highest(
        keymap.of([
          {
            key: "Escape",
            run: (v) => {
              if (closeSignatureHelp(v)) return true;
              if (completionStatus(v.state) === "active" && currentCompletions(v.state).length > 0) return false;
              closeCompletion(v);
              k().escape();
              return true;
            },
          },
        ]),
      ),
      props.language === "csharp" ? languageServices(() => latest.current.ask) : [],
      keymap.of([
        // Running closes an open completion list, so the next Enter starts a new line.
        { key: "Mod-Enter", run: (v) => (closeCompletion(v), k().run(), true), preventDefault: true },
        { key: "Shift-Enter", run: (v) => (closeCompletion(v), k().runAndNext(), true), preventDefault: true },
        { key: "Mod-Shift-Enter", run: (v) => (closeCompletion(v), k().runAll(), true), preventDefault: true },
        { key: "Mod-s", run: () => (k().save(), true), preventDefault: true },
        { key: "Escape", run: () => (k().escape(), true) },
        {
          key: "ArrowUp",
          run: (v) => v.state.selection.main.empty && v.state.doc.lineAt(v.state.selection.main.head).number === 1 && k().up(),
        },
        {
          key: "ArrowDown",
          run: (v) => v.state.selection.main.empty && v.state.doc.lineAt(v.state.selection.main.head).number === v.state.doc.lines && k().down(),
        },
        ...closeBracketsKeymap,
        ...defaultKeymap,
        ...historyKeymap,
        ...searchKeymap,
        indentWithTab,
      ]),
      EditorView.updateListener.of((u) => {
        if (u.docChanged) latest.current.onChange(u.state.doc.toString());
        if (u.focusChanged && u.view.hasFocus) latest.current.onFocus();
      }),
    ];
    const v = new EditorView({ parent: host.current!, state: EditorState.create({ doc: props.value, extensions }) });
    view.current = v;
    return () => {
      v.destroy();
      view.current = null;
    };
    // The editor is created once; later prop changes are applied by the effects below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Text that changed elsewhere (another tab, the file on disk): replace it, keeping the cursor if possible.
  useEffect(() => {
    const v = view.current;
    if (!v) return;
    const current = v.state.doc.toString();
    if (current === props.value) return;
    const head = Math.min(v.state.selection.main.head, props.value.length);
    v.dispatch({ changes: { from: 0, to: current.length, insert: props.value }, selection: { anchor: head } });
  }, [props.value]);

  useEffect(() => {
    const v = view.current;
    if (!v) return;
    v.dispatch(setDiagnostics(v.state, toCm(v.state, props.diagnostics)));
  }, [props.diagnostics, props.value]);

  // Layout effect: focus moves in the same task as the key that asked for it, so typing straight
  // after Shift+Enter lands in the new cell.
  useLayoutEffect(() => {
    const v = view.current;
    if (!v || !props.focus) return;
    const pos = props.focus.at === "start" ? 0 : v.state.doc.length;
    v.dispatch({ selection: { anchor: pos }, scrollIntoView: true });
    v.focus();
  }, [props.focus?.serial]); // eslint-disable-line react-hooks/exhaustive-deps

  return <div className="editor" ref={host} />;
}
