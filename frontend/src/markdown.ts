import MarkdownIt from "markdown-it";

// Raw HTML in Markdown is off: Markdown cells and Md() output render as plain Markdown.
// Explicit HTML output (Html(...)) is a separate MIME type and is shown as HTML.
const md = new MarkdownIt({ html: false, linkify: true, typographer: false });

const defaultLink = md.renderer.rules.link_open ?? ((tokens, i, options, _env, self) => self.renderToken(tokens, i, options));
md.renderer.rules.link_open = (tokens, i, options, env, self) => {
  // Links leave the editor rather than replacing it.
  tokens[i].attrSet("target", "_blank");
  tokens[i].attrSet("rel", "noopener noreferrer");
  return defaultLink(tokens, i, options, env, self);
};

export function renderMarkdown(text: string): string {
  return md.render(text);
}
