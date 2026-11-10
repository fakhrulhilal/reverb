import { spawnSync } from "node:child_process";
import { copyFileSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { test as base, expect } from "@playwright/test";
import { cli, repo, tempFolder } from "./reverb";

/** Runs `reverb export --html` on a copy of a fixture and returns the page's file:// address. */
function exportFixture(name: string, ...extra: string[]): string {
  const dir = tempFolder();
  const file = join(dir, `${name}.cs`);
  copyFileSync(join(repo, "tests", "fixtures", `${name}.cs`), file);
  const r = spawnSync("dotnet", [cli, "export", file, "--html", ...extra], { cwd: dir, encoding: "utf8" });
  if (r.status !== 0) throw new Error(`export failed (${r.status}):\n${r.stdout}\n${r.stderr}`);
  return pathToFileURL(join(dir, `${name}.html`)).href;
}

base("an exported page shows charts, tables and code with no server", async ({ page }) => {
  base.setTimeout(120_000);
  // Anything fetched from the network would fail this test.
  await page.route(/^https?:/, (route) => route.abort());
  await page.goto(exportFixture("charts"));

  await expect(page).toHaveTitle("Charts");
  await expect(page.getByRole("heading", { name: "Charts" })).toBeVisible();
  const charts = page.getByTestId("chart");
  await expect(charts).toHaveCount(3);
  for (let i = 0; i < 3; i++) await expect(charts.nth(i).locator("svg")).toBeVisible({ timeout: 30_000 });
  await expect(charts.first().locator("svg")).toContainText("Daily total");

  // Code is there, folded away under each output.
  const code = page.locator("details.export-source").filter({ hasText: "Chart.Line(" });
  await expect(code.locator("pre")).toBeHidden();
  await code.locator("summary").click();
  await expect(code.locator("pre")).toContainText("Chart.Line(days");
  await expect(code.locator(".tok-string")).toHaveText('"Daily total"');
});

base("an exported page of the sales sample has its table, and --no-code leaves code out", async ({ page }) => {
  base.setTimeout(120_000);
  await page.route(/^https?:/, (route) => route.abort());
  await page.goto(exportFixture("sales", "--no-code"));
  await expect(page.locator("table.output-table")).toBeVisible();
  await expect(page.locator("details.export-source")).toHaveCount(0);
  await expect(page.getByRole("slider", { name: "Minimum order" })).toBeDisabled();
});
