import { readFileSync } from "node:fs";
import { test, expect, codeCells, stdoutOf } from "./reverb";

test("command mode adds and deletes cells", async ({ page, reverb }) => {
  const cells = codeCells(page);
  const before = await cells.count();

  await page.getByRole("button", { name: "+ Code cell" }).click();
  await page.keyboard.type('Console.WriteLine("one");');
  await page.keyboard.press("ControlOrMeta+Enter");
  await expect(stdoutOf(cells.last())).toHaveText("one");

  // Esc leaves the editor; B adds a cell below and focuses it.
  await page.keyboard.press("Escape");
  await page.keyboard.press("b");
  await expect(cells).toHaveCount(before + 2);
  await page.keyboard.type('Console.WriteLine("two");');
  await page.keyboard.press("ControlOrMeta+Enter");
  await expect(stdoutOf(cells.last())).toHaveText("two");

  // D D deletes the selected cell, and the file follows.
  await page.keyboard.press("Escape");
  await page.keyboard.press("d");
  await page.keyboard.press("d");
  await expect(cells).toHaveCount(before + 1);
  await expect.poll(() => readFileSync(reverb.file, "utf8")).not.toContain('"two"');
  expect(readFileSync(reverb.file, "utf8")).toContain('"one"');
});

test("markdown cells render and edit on double-click", async ({ page, reverb }) => {
  await page.getByRole("button", { name: "+ Markdown cell" }).click();
  await page.keyboard.type("## Results\nAll **good**.");
  await page.keyboard.press("ControlOrMeta+Enter");

  const cell = page.locator('section.cell[data-kind="markdown"]').last();
  await expect(cell.getByRole("heading", { name: "Results" })).toBeVisible();
  await expect(cell.locator("strong")).toHaveText("good");
  await expect(cell.locator(".cm-editor")).toHaveCount(0);

  await cell.locator(".markdown-cell").dblclick();
  await expect(cell.locator(".cm-editor")).toBeVisible();
  await expect.poll(() => readFileSync(reverb.file, "utf8")).toContain("// ## Results");
});
