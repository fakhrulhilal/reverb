import { readFileSync } from "node:fs";
import { test, expect, codeCells, setCode, stdoutOf } from "./reverb";

test("adding a package from the panel rebuilds the kernel and the package works", async ({ page, reverb }) => {
  test.setTimeout(240_000);

  await page.getByRole("button", { name: /^Packages/ }).click();
  const panel = page.getByRole("region", { name: "Packages" });
  await expect(panel.getByText("No packages yet.")).toBeVisible();
  await panel.getByLabel("Package name").fill("Humanizer");
  await panel.getByLabel("Package version").fill("2.14.1");
  await panel.getByRole("button", { name: "Add" }).click();

  await expect(panel.locator('li[data-package="Humanizer"]')).toBeVisible();
  await expect(page.getByRole("button", { name: "Packages (1)" })).toBeVisible();
  await expect.poll(() => readFileSync(reverb.file, "utf8")).toContain("#:package Humanizer@2.14.1");
  await expect(page.getByTestId("kernel-state")).toHaveText(/kernel (ready|busy)/, { timeout: 180_000 });

  await setCode(page.locator('section.cell[data-kind="setup"]'), "using Reverb;\nusing Humanizer;");
  await page.getByRole("button", { name: "+ Code cell" }).click();
  await page.keyboard.type('Console.WriteLine("some_text".Humanize());');
  await page.keyboard.press("ControlOrMeta+Shift+Enter");
  await expect(stdoutOf(codeCells(page).last())).toHaveText("some text", { timeout: 60_000 });

  // Removing it takes the package away again.
  await panel.getByRole("button", { name: "Remove Humanizer" }).click();
  await expect(panel.getByText("No packages yet.")).toBeVisible();
  await expect.poll(() => readFileSync(reverb.file, "utf8")).not.toContain("Humanizer@");
  await expect(codeCells(page).last()).toHaveAttribute("data-status", /error|upstream-error/, { timeout: 60_000 });
});

test("a package that can't be found says so", async ({ page, reverb: _ }) => {
  test.setTimeout(120_000);
  await page.getByRole("button", { name: /^Packages/ }).click();
  const panel = page.getByRole("region", { name: "Packages" });
  await panel.getByLabel("Package name").fill("Reverb.Tests.No.Such.Package");
  await panel.getByLabel("Package version").fill("1.0.0");
  await panel.getByRole("button", { name: "Add" }).click();
  await expect(page.getByTestId("kernel-dead")).toContainText("Unable to find package Reverb.Tests.No.Such.Package", { timeout: 90_000 });

  await panel.getByRole("button", { name: "Remove Reverb.Tests.No.Such.Package" }).click();
  await expect(page.getByTestId("kernel-state")).toHaveText(/kernel (ready|busy)/, { timeout: 60_000 });
  await expect(page.getByTestId("kernel-dead")).toHaveCount(0);
});
