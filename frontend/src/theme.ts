import { useSyncExternalStore } from "react";

/** What the user picked. "system" follows the operating system's light or dark setting. */
export type ThemeChoice = "system" | "light" | "dark";

const KEY = "reverb-theme";
const media = window.matchMedia("(prefers-color-scheme: dark)");
const listeners = new Set<() => void>();

function stored(): ThemeChoice {
  try {
    const v = localStorage.getItem(KEY);
    return v === "light" || v === "dark" ? v : "system";
  } catch {
    return "system";
  }
}

let choice: ThemeChoice = stored();

function apply() {
  if (choice === "system") delete document.documentElement.dataset.theme;
  else document.documentElement.dataset.theme = choice;
  for (const l of listeners) l();
}

media.addEventListener("change", () => {
  for (const l of listeners) l();
});
apply();

export function themeChoice(): ThemeChoice {
  return choice;
}

export function setThemeChoice(next: ThemeChoice) {
  choice = next;
  try {
    if (next === "system") localStorage.removeItem(KEY);
    else localStorage.setItem(KEY, next);
  } catch {
    // Storage can be blocked; the choice still holds for this page.
  }
  apply();
}

/** The theme in effect right now. */
export function currentTheme(): "light" | "dark" {
  return choice === "system" ? (media.matches ? "dark" : "light") : choice;
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function useTheme(): { choice: ThemeChoice; theme: "light" | "dark" } {
  const c = useSyncExternalStore(subscribe, themeChoice);
  const theme = useSyncExternalStore(subscribe, currentTheme);
  return { choice: c, theme };
}
