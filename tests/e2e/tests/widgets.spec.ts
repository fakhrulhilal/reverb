import { copyFileSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { test as base, expect, type Page, type Locator } from "@playwright/test";
import { startReverb, stopReverb, tempFolder, cellById, codeCells, setCode, stdoutOf, test } from "./reverb";

const sample = join(import.meta.dirname, "..", "notebooks", "sales.cs");

/** Starts reverb on a copy of the sales explorer sample and opens it. */
const sales = (command: "edit" | "run") =>
  base.extend<{ file: string }>({
    file: [async ({ page }, use) => {
      const file = join(tempFolder(), "sales.cs");
      copyFileSync(sample, file);
      const { reverb, process } = await startReverb(command, file);
      try {
        await page.goto(reverb.url);
        await use(file);
      } finally {
        await stopReverb(process);
      }
    }, { auto: true }],
  });

const runs = async (cell: Locator) => Number(await cell.getAttribute("data-runs"));

/** Total of the Count column in the summary table. */
async function orderCount(page: Page) {
  const counts = await page.locator(`[data-cell-id="summary"]`).locator("table.output-table tbody tr td:nth-child(2)").allInnerTexts();
  return counts.reduce((sum, c) => sum + Number(c), 0);
}

/** Drags a range input's thumb to a fraction of its width with the mouse. */
async function drag(slider: Locator, to: number) {
  const box = (await slider.boundingBox())!;
  const page = slider.page();
  const value = Number(await slider.inputValue());
  const min = Number(await slider.getAttribute("min"));
  const max = Number(await slider.getAttribute("max"));
  const y = box.y + box.height / 2;
  await page.mouse.move(box.x + ((value - min) / (max - min)) * box.width, y);
  await page.mouse.down();
  for (let i = 1; i <= 10; i++) await page.mouse.move(box.x + (((value - min) / (max - min)) + (to - (value - min) / (max - min)) * (i / 10)) * box.width, y);
  await page.mouse.up();
}

sales("edit")("dragging the slider re-runs only the summary", async ({ page }) => {
  const data = cellById(page, "data");
  const threshold = cellById(page, "threshold");
  const summary = cellById(page, "summary");
  await expect(summary.locator("table.output-table")).toBeVisible();
  await expect(summary).toHaveAttribute("data-status", "idle");
  const before = await orderCount(page);
  const [dataRuns, thresholdRuns, summaryRuns] = [await runs(data), await runs(threshold), await runs(summary)];

  const slider = threshold.getByRole("slider", { name: "Minimum order" });
  await drag(slider, 0.9);
  await expect(threshold.locator(".widget-value")).toHaveText(/^(8[89]\d|9[01]\d)$/);
  await expect.poll(() => orderCount(page)).toBeLessThan(before);
  await expect(summary).toHaveAttribute("data-status", "idle");

  expect(await runs(summary)).toBeGreaterThan(summaryRuns);
  expect(await runs(data)).toBe(dataRuns);
  expect(await runs(threshold)).toBe(thresholdRuns);
});

sales("run")("app mode shows outputs and widgets, hides code and can't be edited", async ({ page, file }) => {
  const original = readFileSync(file, "utf8");
  await expect(page.getByRole("heading", { name: "Sales explorer" })).toBeVisible();
  await expect(page.locator("table.output-table")).toBeVisible();
  await expect(page.locator(".cm-editor")).toHaveCount(0);
  await expect(page.getByRole("button")).toHaveCount(0);
  const html = await page.content();
  expect(html).not.toContain("Enumerable.Range");
  expect(html).not.toContain("record Order");

  const before = await orderCount(page);
  expect(before).toBeGreaterThan(0);
  await drag(page.getByRole("slider", { name: "Minimum order" }), 0.1);
  await expect.poll(() => orderCount(page)).toBeGreaterThan(before);
  expect(readFileSync(file, "utf8")).toBe(original);
});

test("every widget drives its readers", async ({ page, reverb: _ }) => {
  await page.getByRole("button", { name: "+ Code cell" }).click();
  await page.keyboard.type(
    'var region = Ui.Dropdown("Region", ["West", "East", "Central"]);\n' +
      'var query = Ui.Text("Search", value: "");\n' +
      'var all = Ui.Checkbox("Show all");\n' +
      'var rows = Ui.Number("Rows", 10, min: 1, max: 100);\n' +
      'var go = Ui.Button("Refresh");',
  );
  await page.keyboard.press("Shift+Enter");
  await page.keyboard.type('Console.WriteLine($"{region.Value}|{query.Value}|{all.Value}|{rows.Value}|{go.Value}");');
  await page.keyboard.press("ControlOrMeta+Enter");

  const controls = codeCells(page).nth(-2);
  const reader = codeCells(page).last();
  await expect(stdoutOf(reader)).toHaveText("West||False|10|0");

  await controls.getByRole("combobox", { name: "Region" }).selectOption("Central");
  await expect(stdoutOf(reader)).toHaveText("Central||False|10|0");
  await controls.getByRole("textbox", { name: "Search" }).fill("tea");
  await expect(stdoutOf(reader)).toHaveText("Central|tea|False|10|0");
  await controls.getByRole("checkbox", { name: "Show all" }).check();
  await expect(stdoutOf(reader)).toHaveText("Central|tea|True|10|0");
  await controls.getByRole("spinbutton", { name: "Rows" }).fill("42");
  await expect(stdoutOf(reader)).toHaveText("Central|tea|True|42|0");
  await controls.getByRole("button", { name: "Refresh" }).click();
  await controls.getByRole("button", { name: "Refresh" }).click();
  await expect(stdoutOf(reader)).toHaveText("Central|tea|True|42|2");

  // Out of range: the box says so and nothing re-runs.
  const runsBefore = await runs(reader);
  await controls.getByRole("spinbutton", { name: "Rows" }).fill("500");
  await expect(controls.locator(".widget-hint")).toHaveText("1 to 100");
  await page.waitForTimeout(600);
  expect(await runs(reader)).toBe(runsBefore);

  // Editing the widgets' cell keeps values that still fit.
  await setCode(reader, 'Console.WriteLine($"{region.Value}/{query.Value}");');
  await page.keyboard.press("ControlOrMeta+Enter");
  await expect(stdoutOf(reader)).toHaveText("Central/tea");
});
