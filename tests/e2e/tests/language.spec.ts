import { test, expect, codeCells, editorOf, stdoutOf } from "./reverb";

test("completions list a variable from an earlier cell; hover and signature help work", async ({ page, reverb: _ }) => {
  await page.getByRole("button", { name: "+ Code cell" }).click();
  await page.keyboard.type("var orders = new List<int> { 1, 2, 3 };");
  await page.keyboard.press("Shift+Enter");

  // Typing a name brings up completions from the kernel, including the earlier cell's variable.
  await page.keyboard.type("var n = ord");
  const popup = page.locator(".cm-tooltip-autocomplete");
  await expect(popup.getByRole("option", { name: /^orders/ })).toBeVisible({ timeout: 15_000 });
  await page.keyboard.press("Enter");
  const second = codeCells(page).last();
  await expect(editorOf(second)).toHaveText("var n = orders");

  // Member completions after a dot, narrowed as the user types.
  await page.keyboard.type(".");
  await expect(popup.getByRole("option", { name: /^Add/ }).first()).toBeVisible({ timeout: 15_000 });
  await page.keyboard.type("Cou");
  await expect(popup.getByRole("option", { name: /^Count/ }).first()).toBeVisible({ timeout: 15_000 });
  await page.keyboard.press("Escape");
  for (let i = 0; i < 4; i++) await page.keyboard.press("Backspace");

  // Signature help while typing arguments.
  await page.keyboard.type(".Count + Math.Max(1, ");
  await expect(page.getByTestId("signature-help")).toContainText("int val2", { timeout: 15_000 });
  await expect(page.getByTestId("signature-help").locator("strong")).toHaveText("int val2");
  await page.keyboard.type("2);");
  await page.keyboard.press("ControlOrMeta+Enter");

  // Hover shows the type and where the variable comes from.
  const word = await editorOf(second).evaluate((el) => {
    const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
    for (let n = walker.nextNode(); n; n = walker.nextNode()) {
      const at = n.textContent!.indexOf("orders");
      if (at < 0) continue;
      const range = document.createRange();
      range.setStart(n, at + 2);
      range.setEnd(n, at + 3);
      const box = range.getBoundingClientRect();
      return { x: box.x + box.width / 2, y: box.y + box.height / 2 };
    }
    return null;
  });
  await page.mouse.move(word!.x, word!.y);
  await expect(page.locator(".lang-hover")).toContainText("Defined in cell", { timeout: 15_000 });
  await expect(page.locator(".lang-hover")).toContainText("List<int>");
});

test("a misplaced using has a fix button that moves it to setup", async ({ page, reverb: _ }) => {
  await page.getByRole("button", { name: "+ Code cell" }).click();
  await page.keyboard.type("var a = 6;");
  await page.keyboard.press("Shift+Enter");
  await page.keyboard.type("using System.Text;\nConsole.WriteLine(new StringBuilder().Append(a * 7));");
  await page.keyboard.press("ControlOrMeta+Enter");

  const cell = codeCells(page).last();
  const fix = cell.getByRole("button", { name: "Move to the setup cell" });
  await expect(fix).toBeVisible({ timeout: 15_000 });
  await fix.click();

  await expect(editorOf(page.locator('section.cell[data-kind="setup"]'))).toContainText("using System.Text;");
  await expect(editorOf(cell)).not.toContainText("using System.Text;");
  await expect(stdoutOf(cell)).toHaveText("42", { timeout: 30_000 });
});

test("running a cell closes the completion list, so Enter starts a new line", async ({ page, reverb: _ }) => {
  await page.getByRole("button", { name: "+ Code cell" }).click();
  await page.keyboard.type("var total = 1;");
  await page.keyboard.press("Shift+Enter");
  await page.keyboard.type("var t = tot");
  const popup = page.locator(".cm-tooltip-autocomplete");
  await expect(popup.getByRole("option", { name: /^total/ })).toBeVisible({ timeout: 15_000 });
  await page.keyboard.press("ControlOrMeta+Enter");
  await expect(popup).toHaveCount(0);
  await page.keyboard.press("Enter");
  await expect(editorOf(codeCells(page).last()).locator(".cm-line")).toHaveCount(2);
});
