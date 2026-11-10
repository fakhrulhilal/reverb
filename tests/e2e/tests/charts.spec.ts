import { test, expect, codeCells } from "./reverb";

test("a chart draws, follows a slider, and follows the theme", async ({ page, reverb: _ }) => {
  await page.getByRole("button", { name: "+ Code cell" }).click();
  await page.keyboard.type('var n = Ui.Slider("Points", min: 2, max: 20, value: 5);');
  await page.keyboard.press("Shift+Enter");
  await page.keyboard.type('Chart.Line(Enumerable.Range(1, (int)n.Value).Select(i => (X: i, Y: i * i)), x: p => p.X, y: p => p.Y, title: "Squares");');
  await page.keyboard.press("ControlOrMeta+Enter");

  const chart = codeCells(page).last().getByTestId("chart");
  await expect(chart.locator("svg")).toBeVisible({ timeout: 30_000 });
  await expect(chart.locator("svg")).toContainText("Squares");
  // Five points with the slider at 5.
  await expect(chart.locator("svg path.mark-symbol, svg .mark-symbol path")).toHaveCount(5);

  // Moving the slider redraws the chart with more points.
  const slider = codeCells(page).nth(-2).getByRole("slider", { name: "Points" });
  await slider.focus();
  for (let i = 0; i < 3; i++) await page.keyboard.press("ArrowRight");
  await expect(chart.locator("svg path.mark-symbol, svg .mark-symbol path")).toHaveCount(8, { timeout: 30_000 });

  // The theme button cycles system → light → dark and the choice survives a reload.
  const theme = page.getByTestId("theme");
  await expect(theme).toHaveText("Theme: system");
  await theme.click();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "light");
  await theme.click();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  const background = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
  expect(background).toBe("rgb(22, 22, 24)");
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  await expect(page.getByTestId("theme")).toHaveText("Theme: dark");
  await expect(codeCells(page).last().getByTestId("chart").locator("svg")).toBeVisible({ timeout: 30_000 });
});
