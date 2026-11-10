import { setThemeChoice, useTheme, type ThemeChoice } from "./theme";

const next: Record<ThemeChoice, ThemeChoice> = { system: "light", light: "dark", dark: "system" };
const label: Record<ThemeChoice, string> = { system: "Theme: system", light: "Theme: light", dark: "Theme: dark" };

/** Cycles system → light → dark. The choice is kept in this browser. */
export function ThemeButton({ className }: { className?: string }) {
  const { choice } = useTheme();
  return (
    <button className={className} onClick={() => setThemeChoice(next[choice])} title="Light, dark, or follow the system" data-testid="theme">
      {label[choice]}
    </button>
  );
}
