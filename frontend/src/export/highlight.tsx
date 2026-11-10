import type { ReactNode } from "react";
import { StreamLanguage } from "@codemirror/language";
import { csharp } from "@codemirror/legacy-modes/mode/clike";
import { highlightCode, tagHighlighter, tags } from "@lezer/highlight";

const parser = StreamLanguage.define(csharp).parser;

// The same token colours as the editor (see the tok-* rules in export.css).
const highlighter = tagHighlighter([
  { tag: tags.keyword, class: "tok-keyword" },
  { tag: [tags.string, tags.special(tags.string)], class: "tok-string" },
  { tag: [tags.number, tags.bool, tags.null, tags.atom], class: "tok-number" },
  { tag: tags.comment, class: "tok-comment" },
  { tag: [tags.typeName, tags.className], class: "tok-type" },
  { tag: [tags.definition(tags.variableName), tags.function(tags.variableName)], class: "tok-def" },
  { tag: tags.meta, class: "tok-meta" },
]);

/** C# code as highlighted spans, without an editor. */
export function HighlightedCode({ code }: { code: string }) {
  const nodes: ReactNode[] = [];
  highlightCode(
    code,
    parser.parse(code),
    highlighter,
    (text, classes) => nodes.push(classes ? <span key={nodes.length} className={classes}>{text}</span> : text),
    () => nodes.push("\n"),
  );
  return (
    <pre className="export-code">
      <code>{nodes}</code>
    </pre>
  );
}
