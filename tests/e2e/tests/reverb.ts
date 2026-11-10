import { test as base, expect, type Page, type Locator } from "@playwright/test";
import { spawn, type ChildProcess } from "node:child_process";
import { mkdtempSync, writeFileSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";

export const repo = resolve(import.meta.dirname, "..", "..", "..");
const configuration = process.env.REVERB_CONFIGURATION ?? "Debug";
export const cli = process.env.REVERB_CLI ?? join(repo, "src", "Reverb.Cli", "bin", configuration, "net10.0", "Reverb.Cli.dll");

export interface Reverb {
  /** The editor's address, token included. */
  url: string;
  /** The notebook file. */
  file: string;
  /** Everything the process printed, for failure messages. */
  log: () => string;
}

/** A temp folder for one test's notebook, with a stopper so parent build files don't leak in. */
export function tempFolder(): string {
  const dir = mkdtempSync(join(tmpdir(), "reverb-e2e-"));
  writeFileSync(join(dir, "Directory.Build.props"), "<Project />");
  return dir;
}

/** Runs `reverb <command> <file> --no-browser` and waits for the address it prints. */
export async function startReverb(command: "new" | "edit" | "run", file: string): Promise<{ reverb: Reverb; process: ChildProcess }> {
  if (!existsSync(cli)) throw new Error(`Build the CLI first (dotnet build): ${cli} doesn't exist.`);
  const child = spawn("dotnet", [cli, command, file, "--no-browser"], {
    cwd: join(file, ".."),
    // Kernels built for packages go under artifacts/ (shared between runs), not the user's ~/.reverb.
    env: { ...process.env, DOTNET_NOLOGO: "1", DOTNET_CLI_TELEMETRY_OPTOUT: "1", REVERB_KERNEL_CACHE: join(repo, "artifacts", "kernels") },
  });
  let output = "";
  const url = await new Promise<string>((ok, fail) => {
    const timer = setTimeout(() => fail(new Error(`reverb didn't start in time:\n${output}`)), 60_000);
    const onData = (chunk: Buffer) => {
      output += chunk.toString();
      const m = output.match(/http:\/\/127\.0\.0\.1:\d+\/\?token=[0-9a-f]+/);
      if (m) {
        clearTimeout(timer);
        ok(m[0]);
      }
    };
    child.stdout.on("data", onData);
    child.stderr.on("data", (chunk: Buffer) => (output += chunk.toString()));
    child.on("exit", (code) => {
      clearTimeout(timer);
      fail(new Error(`reverb exited with ${code}:\n${output}`));
    });
  });
  child.stdout.on("data", (chunk: Buffer) => (output += chunk.toString()));
  return { reverb: { url, file, log: () => output }, process: child };
}

export async function stopReverb(child: ChildProcess) {
  if (child.exitCode !== null) return;
  const exited = new Promise((ok) => child.on("exit", ok));
  child.kill(process.platform === "win32" ? undefined : "SIGINT");
  const timeout = new Promise((ok) => setTimeout(ok, 10_000));
  await Promise.race([exited, timeout]);
  if (child.exitCode === null) child.kill("SIGKILL");
}

/** A test with a fresh notebook from `reverb new`, open in the page. */
export const test = base.extend<{ reverb: Reverb }>({
  reverb: async ({ page }, use) => {
    const file = join(tempFolder(), "notebook.cs");
    const { reverb, process } = await startReverb("new", file);
    try {
      await page.goto(reverb.url);
      await expect(page.getByTestId("kernel-state")).toHaveText(/kernel (ready|busy)/);
      await use(reverb);
    } finally {
      await stopReverb(process);
    }
  },
});

export { expect };

// ---- page helpers ----------------------------------------------------------------------------

export function codeCells(page: Page): Locator {
  return page.locator('section.cell[data-kind="code"]');
}

export function cellById(page: Page, id: string): Locator {
  return page.locator(`section.cell[data-cell-id="${id}"]`);
}

export function editorOf(cell: Locator): Locator {
  return cell.locator(".cm-content");
}

/** Replaces a cell's code by typing, as a person would. */
export async function setCode(cell: Locator, code: string) {
  const editor = editorOf(cell);
  await editor.click();
  await editor.press("ControlOrMeta+a");
  await editor.page().keyboard.type(code);
}

export function stdoutOf(cell: Locator): Locator {
  return cell.locator("pre.stream.stdout");
}
