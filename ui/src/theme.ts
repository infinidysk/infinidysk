import { useSyncExternalStore } from "react";

export type Theme = "system" | "light" | "dark";

const KEY = "theme";
const media = window.matchMedia("(prefers-color-scheme: dark)");
const listeners = new Set<() => void>();

function stored(): Theme {
  const value = localStorage.getItem(KEY);
  return value === "light" || value === "dark" ? value : "system";
}

function apply() {
  const theme = stored();
  document.documentElement.classList.toggle("dark", theme === "dark" || (theme === "system" && media.matches));
  for (const listener of listeners) listener();
}

apply();
media.addEventListener("change", apply);

export function setTheme(theme: Theme) {
  if (theme === "system") localStorage.removeItem(KEY);
  else localStorage.setItem(KEY, theme);
  apply();
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function useTheme(): Theme {
  return useSyncExternalStore(subscribe, stored);
}

/** The applied scheme, after resolving `system`. */
export function useDark(): boolean {
  return useSyncExternalStore(subscribe, () => document.documentElement.classList.contains("dark"));
}
