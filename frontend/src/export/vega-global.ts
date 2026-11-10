// In an exported page, vega-embed is a separate script that sets window.reverbVegaEmbed. It's only
// included when the notebook has charts, so pages without charts stay small.
type Embed = (el: HTMLElement, spec: unknown, options: unknown) => Promise<{ finalize(): void }>;

export default function embed(el: HTMLElement, spec: unknown, options: unknown) {
  const real = (window as unknown as { reverbVegaEmbed?: Embed }).reverbVegaEmbed;
  if (!real) return Promise.reject(new Error("the chart library isn't in this page"));
  return real(el, spec, options);
}
