import { readFileSync, writeFileSync } from "node:fs";
import { test, expect, codeCells, editorOf, setCode, stdoutOf } from "./reverb";

test("add three dependent cells, edit the first, the third updates", async ({ page, reverb }) => {
  const before = await codeCells(page).count();

  // A new cell gets focus; Shift+Enter runs it and makes the next one.
  await page.getByRole("button", { name: "+ Code cell" }).click();
  await page.keyboard.type("var a = 1;");
  await page.keyboard.press("Shift+Enter");
  await page.keyboard.type("var b = a * 10;");
  await page.keyboard.press("Shift+Enter");
  await page.keyboard.type("Console.WriteLine(b + 1);");
  await page.keyboard.press("ControlOrMeta+Enter");

  const cells = codeCells(page);
  await expect(cells).toHaveCount(before + 3);
  const [first, , third] = [cells.nth(before), cells.nth(before + 1), cells.nth(before + 2)];
  await expect(stdoutOf(third)).toHaveText("11");
  await expect(first.getByText("used by")).toBeVisible();

  await setCode(first, "var a = 5;");
  await expect(first.locator(".edited")).toBeVisible();
  await expect(stdoutOf(third)).toHaveText("11"); // typing alone doesn't run anything
  await page.keyboard.press("ControlOrMeta+Enter");
  await expect(stdoutOf(third)).toHaveText("51");
  await expect(first.locator(".edited")).toHaveCount(0);

  // The file on disk has the cells, and autosave catches up.
  await expect(page.getByTestId("save-state")).toHaveText("saved");
  await expect.poll(() => readFileSync(reverb.file, "utf8")).toContain("var a = 5;");
  expect(readFileSync(reverb.file, "utf8")).toContain("Console.WriteLine(b + 1);");
});

test("editing the file on disk updates the editor", async ({ page, reverb }) => {
  await page.getByRole("button", { name: "+ Code cell" }).click();
  await page.keyboard.type("var n = 2;");
  await page.keyboard.press("Shift+Enter");
  await page.keyboard.type("Console.WriteLine(n * n);");
  await page.keyboard.press("ControlOrMeta+Enter");
  const square = codeCells(page).last();
  await expect(stdoutOf(square)).toHaveText("4");
  await expect.poll(() => readFileSync(reverb.file, "utf8")).toContain("Console.WriteLine(n * n);");

  // Another program (an editor, an agent) changes the file.
  const text = readFileSync(reverb.file, "utf8");
  writeFileSync(reverb.file, text.replace("var n = 2;", "var n = 9;"));

  await expect(editorOf(codeCells(page).nth(-2))).toHaveText("var n = 9;");
  await expect(stdoutOf(square)).toHaveText("81");
  await expect(page.locator(".toast")).toContainText("changed on disk");
});

test("kill the kernel mid-run and recover", async ({ page, reverb: _ }) => {
  await page.getByRole("button", { name: "+ Code cell" }).click();
  await page.keyboard.type("var x = 6;");
  await page.keyboard.press("Shift+Enter");
  await page.keyboard.type("var spin = 0;\nwhile (true) { spin++; }");
  await page.keyboard.press("Shift+Enter");
  await page.keyboard.type("Console.WriteLine(x * 7);");
  await page.keyboard.press("ControlOrMeta+Enter");

  const cells = codeCells(page);
  const loop = cells.nth(-2);
  const answer = cells.last();
  await expect(loop).toHaveAttribute("data-status", "running");
  await expect(loop.getByTestId("cell-status")).toHaveText(/running \d/);

  // The loop never checks for cancellation, so only killing the kernel stops it.
  await page.getByRole("button", { name: "Restart" }).click();
  await expect(page.getByTestId("kernel-state")).toHaveText("kernel ready");
  await expect(loop).toHaveAttribute("data-status", "stale");

  await setCode(loop, "var spin = 0;");
  await page.getByRole("button", { name: "Run all" }).click();
  await expect(stdoutOf(answer)).toHaveText("42");
  await expect(loop).toHaveAttribute("data-status", "idle");
});
