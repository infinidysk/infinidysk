import type { ReactNode } from "react";

/** Shared content wrapper for all settings tabs. */
export function SettingsPanel({
  children,
  className = "",
}: {
  children: ReactNode;
  className?: string;
}) {
  return <div className={`w-full min-w-0 ${className}`}>{children}</div>;
}

/** Consistent vertical rhythm for a settings page body. */
export function SettingsPage({
  children,
  className = "",
}: {
  children: ReactNode;
  className?: string;
}) {
  return <div className={`flex w-full flex-col gap-6 ${className}`}>{children}</div>;
}

/** One settings block (label / control / help). */
export function SettingsSection({
  children,
  className = "",
}: {
  children: ReactNode;
  className?: string;
}) {
  return <section className={`flex flex-col gap-2 ${className}`}>{children}</section>;
}

/** Intro copy under the page title. */
export function SettingsIntro({
  children,
  className = "",
}: {
  children: ReactNode;
  className?: string;
}) {
  return <p className={`text-sm leading-relaxed text-base-content/55 ${className}`}>{children}</p>;
}
