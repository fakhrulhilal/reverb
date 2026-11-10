import { useState } from "react";
import type { Package } from "./protocol";
import type { NotebookStore } from "./store";

// Same rules the server applies; checked here too so a typo is caught before anything is sent.
const validName = (name: string) => /^[A-Za-z0-9._-]+$/.test(name);
const validVersion = (version: string) => /^[A-Za-z0-9.+*\-[\](),]+$/.test(version);

/** The notebook's #:package lines. Changing them rebuilds the kernel. */
export function PackagesPanel(props: { packages: Package[]; store: NotebookStore; onClose: () => void }) {
  const { packages, store } = props;
  const [name, setName] = useState("");
  const [version, setVersion] = useState("");
  const [problem, setProblem] = useState<string | null>(null);

  const add = () => {
    const n = name.trim();
    const v = version.trim();
    if (!validName(n)) return setProblem("Package names use letters, digits, '.', '_' and '-'.");
    if (!validVersion(v)) return setProblem("Give a version, e.g. 2.14.1 ('*' takes the latest).");
    const rest = packages.filter((p) => p.name.toLowerCase() !== n.toLowerCase());
    store.setPackages([...rest, { name: n, version: v }]);
    setName("");
    setVersion("");
    setProblem(null);
  };

  const remove = (p: Package) => store.setPackages(packages.filter((q) => q !== p));

  const changeVersion = (p: Package, v: string) => {
    const trimmed = v.trim();
    if (trimmed === (p.version ?? "")) return;
    if (!validVersion(trimmed)) return setProblem(`'${v}' isn't a version.`);
    setProblem(null);
    store.setPackages(packages.map((q) => (q === p ? { ...q, version: trimmed } : q)));
  };

  return (
    <section className="packages" aria-label="Packages">
      <header>
        <strong>NuGet packages</strong>
        <span className="hint">Saved as <code>#:package</code> lines. Changing them rebuilds the kernel.</span>
        <button className="icon" aria-label="Close packages" onClick={props.onClose}>
          ✕
        </button>
      </header>
      {packages.length === 0 && <p className="empty">No packages yet.</p>}
      <ul>
        {packages.map((p) => (
          <li key={p.name} data-package={p.name}>
            <span className="name">{p.name}</span>
            <input
              className="version"
              aria-label={`${p.name} version`}
              defaultValue={p.version ?? ""}
              key={p.version}
              onBlur={(e) => changeVersion(p, e.target.value)}
              onKeyDown={(e) => e.key === "Enter" && (e.target as HTMLInputElement).blur()}
            />
            <button className="icon danger" aria-label={`Remove ${p.name}`} onClick={() => remove(p)}>
              ✕
            </button>
          </li>
        ))}
      </ul>
      <form
        className="add-package"
        onSubmit={(e) => {
          e.preventDefault();
          add();
        }}
      >
        <input aria-label="Package name" placeholder="Package, e.g. Humanizer" value={name} onChange={(e) => setName(e.target.value)} />
        <input aria-label="Package version" placeholder="Version, e.g. 2.14.1" value={version} onChange={(e) => setVersion(e.target.value)} />
        <button type="submit" disabled={!name.trim() || !version.trim()}>
          Add
        </button>
      </form>
      {problem && <p className="problem">{problem}</p>}
    </section>
  );
}
