---
version: alpha
name: infinidysk
description: >-
  Web UI of infinidysk, a Usenet-backed virtual filesystem, styled after the
  Supabase Studio design language: quiet blue-gray neutrals, 1px borders
  instead of shadows, one blue accent, dense settings pages with a grouped side
  nav, and compact tables and panels. Light and dark themes share the same
  roles; color and component tokens ending in `-dark` hold the dark theme.
colors:
  # Light theme: `:root` in ui/src/index.css
  primary: "oklch(0.5 0.19 248)"
  primary-foreground: "oklch(0.99 0 0)"
  background: "oklch(0.99 0.002 260)"
  foreground: "oklch(0.2 0.02 260)"
  card: "oklch(1 0 0)"
  card-foreground: "oklch(0.2 0.02 260)"
  popover: "oklch(1 0 0)"
  popover-foreground: "oklch(0.2 0.02 260)"
  secondary: "oklch(0.96 0.004 260)"
  secondary-foreground: "oklch(0.25 0.02 260)"
  muted: "oklch(0.96 0.004 260)"
  muted-foreground: "oklch(0.52 0.015 260)"
  accent: "oklch(0.95 0.006 260)"
  accent-foreground: "oklch(0.2 0.02 260)"
  success: "oklch(0.58 0.16 145)"
  warning: "oklch(0.68 0.15 70)"
  success-text: "oklch(0.5 0.14 145)"
  warning-text: "oklch(0.51 0.13 65)"
  destructive: "oklch(0.58 0.22 27)"
  chart-imports: "oklch(0.6 0.17 300)"
  chart-playback: "oklch(0.62 0.12 190)"
  border: "oklch(0.91 0.006 260)"
  input: "oklch(0.89 0.006 260)"
  ring: "oklch(0.5 0.19 248)"
  sidebar: "oklch(0.975 0.003 260)"
  sidebar-foreground: "oklch(0.3 0.02 260)"
  sidebar-primary: "oklch(0.5 0.19 248)"
  sidebar-primary-foreground: "oklch(0.99 0 0)"
  sidebar-accent: "oklch(0.94 0.006 260)"
  sidebar-accent-foreground: "oklch(0.2 0.02 260)"
  sidebar-border: "oklch(0.91 0.006 260)"
  sidebar-ring: "oklch(0.5 0.19 248)"
  # Dark theme: `.dark` in ui/src/index.css
  primary-dark: "oklch(0.64 0.18 248)"
  primary-foreground-dark: "oklch(0.165 0.012 260)"
  background-dark: "oklch(0.165 0.012 260)"
  foreground-dark: "oklch(0.95 0.004 260)"
  card-dark: "oklch(0.195 0.013 260)"
  card-foreground-dark: "oklch(0.95 0.004 260)"
  popover-dark: "oklch(0.21 0.014 260)"
  popover-foreground-dark: "oklch(0.95 0.004 260)"
  secondary-dark: "oklch(0.25 0.014 260)"
  secondary-foreground-dark: "oklch(0.95 0.004 260)"
  muted-dark: "oklch(0.235 0.013 260)"
  muted-foreground-dark: "oklch(0.68 0.015 260)"
  accent-dark: "oklch(0.26 0.016 260)"
  accent-foreground-dark: "oklch(0.97 0.004 260)"
  success-dark: "oklch(0.7 0.17 145)"
  warning-dark: "oklch(0.78 0.14 75)"
  success-text-dark: "oklch(0.7 0.17 145)"
  warning-text-dark: "oklch(0.78 0.14 75)"
  destructive-dark: "oklch(0.68 0.19 24)"
  chart-imports-dark: "oklch(0.68 0.16 300)"
  chart-playback-dark: "oklch(0.72 0.12 190)"
  border-dark: "oklch(1 0 0 / 9%)"
  input-dark: "oklch(1 0 0 / 13%)"
  ring-dark: "oklch(0.64 0.18 248)"
  sidebar-dark: "oklch(0.14 0.012 260)"
  sidebar-foreground-dark: "oklch(0.8 0.01 260)"
  sidebar-primary-dark: "oklch(0.64 0.18 248)"
  sidebar-primary-foreground-dark: "oklch(0.165 0.012 260)"
  sidebar-accent-dark: "oklch(0.22 0.014 260)"
  sidebar-accent-foreground-dark: "oklch(0.97 0.004 260)"
  sidebar-border-dark: "oklch(1 0 0 / 8%)"
  sidebar-ring-dark: "oklch(0.64 0.18 248)"
  # Stall causes: `causes` in ui/src/pages/traces.tsx; same in both themes
  stall-budget: "oklch(0.62 0.17 300)"
  stall-permit: "oklch(0.74 0.15 75)"
  stall-connection: "oklch(0.66 0.18 40)"
  stall-pipeline: "oklch(0.66 0.11 190)"
  stall-latency: "oklch(0.62 0.17 250)"
  stall-transfer: "oklch(0.7 0.13 220)"
  stall-decode: "oklch(0.68 0.16 145)"
  stall-disk: "oklch(0.64 0.12 110)"
  stall-shared: "oklch(0.66 0.14 350)"
  stall-other: "oklch(0.6 0 0)"
typography:
  page-title: { fontFamily: "Geist Variable", fontSize: 20px, fontWeight: 500, lineHeight: 1.4, letterSpacing: -0.025em }
  section-title: { fontFamily: "Geist Variable", fontSize: 18px, fontWeight: 500, lineHeight: 1.56, letterSpacing: -0.025em }
  dialog-title: { fontFamily: "Geist Variable", fontSize: 16px, fontWeight: 500, lineHeight: 1 }
  panel-title: { fontFamily: "Geist Variable", fontSize: 14px, fontWeight: 500, lineHeight: 1.43 }
  body: { fontFamily: "Geist Variable", fontSize: 14px, fontWeight: 400, lineHeight: 1.43 }
  label: { fontFamily: "Geist Variable", fontSize: 14px, fontWeight: 500, lineHeight: 1 }
  caption: { fontFamily: "Geist Variable", fontSize: 12px, fontWeight: 400, lineHeight: 1.33 }
  meta-label: { fontFamily: "Geist Variable", fontSize: 12px, fontWeight: 500, lineHeight: 1.33, letterSpacing: 0.025em }
  button: { fontFamily: "Geist Variable", fontSize: 14px, fontWeight: 500, lineHeight: 1.43 }
  button-sm: { fontFamily: "Geist Variable", fontSize: 12.8px, fontWeight: 500, lineHeight: 1.43 }
  badge: { fontFamily: "Geist Variable", fontSize: 12px, fontWeight: 500, lineHeight: 1.33 }
  numeric: { fontFamily: "Geist Variable", fontSize: 14px, fontWeight: 400, lineHeight: 1.43, fontFeature: "'tnum'" }
  metric: { fontFamily: "Geist Mono Variable", fontSize: 18px, fontWeight: 500, lineHeight: 1.56, fontFeature: "'tnum'" }
  mono: { fontFamily: "Geist Mono Variable", fontSize: 14px, fontWeight: 400, lineHeight: 1.43 }
  mono-caption: { fontFamily: "Geist Mono Variable", fontSize: 12px, fontWeight: 400, lineHeight: 1.33 }
rounded:
  sm: 3.6px
  md: 4.8px
  lg: 6px
  xl: 8.4px
  full: 9999px
spacing:
  xs: 4px
  sm: 8px
  md: 12px
  lg: 16px
  xl: 24px
  2xl: 32px
  3xl: 40px
  control-height: 32px
  control-height-sm: 28px
  table-head-height: 40px
  sidebar-width: 224px
  topbar-height: 48px
  settings-nav-width: 192px
  form-max-width: 576px
  form-wide-max-width: 768px
  content-max-width: 1152px
components:
  # Light theme
  page: { backgroundColor: "{colors.background}", textColor: "{colors.foreground}", typography: "{typography.body}" }
  page-title: { textColor: "{colors.foreground}", typography: "{typography.page-title}" }
  section-title: { textColor: "{colors.foreground}", typography: "{typography.section-title}" }
  page-description: { backgroundColor: "{colors.background}", textColor: "{colors.muted-foreground}", typography: "{typography.body}" }
  meta-label: { textColor: "{colors.muted-foreground}", typography: "{typography.meta-label}" }
  sidebar: { backgroundColor: "{colors.sidebar}", textColor: "{colors.sidebar-foreground}", width: 224px }
  sidebar-item: { textColor: "{colors.sidebar-foreground}", typography: "{typography.body}", rounded: "{rounded.md}", height: 32px, padding: 0 8px }
  sidebar-item-active: { backgroundColor: "{colors.sidebar-accent}", textColor: "{colors.sidebar-accent-foreground}", rounded: "{rounded.md}", height: 32px }
  sidebar-item-active-icon: { textColor: "{colors.sidebar-primary}", size: 16px }
  sidebar-divider: { backgroundColor: "{colors.sidebar-border}", height: 1px }
  sidebar-focus-ring: { backgroundColor: "{colors.sidebar-ring}", width: 3px }
  settings-nav-item-active: { backgroundColor: "{colors.accent}", textColor: "{colors.accent-foreground}", rounded: "{rounded.md}", height: 32px }
  panel: { backgroundColor: "{colors.card}", textColor: "{colors.card-foreground}", rounded: "{rounded.lg}" }
  card: { backgroundColor: "{colors.card}", textColor: "{colors.card-foreground}", rounded: "{rounded.xl}", padding: 16px }
  metric-tile: { backgroundColor: "{colors.card}", textColor: "{colors.foreground}", typography: "{typography.metric}", padding: 12px }
  table-head: { textColor: "{colors.muted-foreground}", typography: "{typography.meta-label}", height: 40px, padding: 0 8px }
  table-cell: { textColor: "{colors.foreground}", typography: "{typography.body}", padding: 8px }
  button-primary: { backgroundColor: "{colors.primary}", textColor: "{colors.primary-foreground}", typography: "{typography.button}", rounded: "{rounded.lg}", height: 32px, padding: 0 10px }
  button-outline: { backgroundColor: "{colors.background}", textColor: "{colors.foreground}", typography: "{typography.button}", rounded: "{rounded.lg}", height: 32px, padding: 0 10px }
  button-outline-hover: { backgroundColor: "{colors.muted}", textColor: "{colors.foreground}" }
  button-secondary: { backgroundColor: "{colors.secondary}", textColor: "{colors.secondary-foreground}", typography: "{typography.button}", rounded: "{rounded.lg}", height: 32px }
  button-ghost: { textColor: "{colors.foreground}", typography: "{typography.button}", rounded: "{rounded.lg}" }
  button-ghost-hover: { backgroundColor: "{colors.muted}", textColor: "{colors.foreground}" }
  button-destructive: { textColor: "{colors.destructive}", typography: "{typography.button}", rounded: "{rounded.lg}", height: 32px }
  button-link: { textColor: "{colors.primary}", typography: "{typography.button}" }
  button-sm: { typography: "{typography.button-sm}", rounded: "{rounded.lg}", height: 28px, padding: 0 10px }
  button-icon-sm: { rounded: "{rounded.lg}", size: 28px }
  input: { textColor: "{colors.foreground}", typography: "{typography.body}", rounded: "{rounded.lg}", height: 32px, padding: 4px 10px }
  field-label: { textColor: "{colors.foreground}", typography: "{typography.label}" }
  field-hint: { textColor: "{colors.muted-foreground}", typography: "{typography.caption}" }
  field-error: { textColor: "{colors.destructive}", typography: "{typography.caption}" }
  switch-off: { backgroundColor: "{colors.input}", rounded: "{rounded.full}", height: 18.4px, width: 32px }
  switch-on: { backgroundColor: "{colors.primary}", rounded: "{rounded.full}", height: 18.4px, width: 32px }
  menu: { backgroundColor: "{colors.popover}", textColor: "{colors.popover-foreground}", rounded: "{rounded.lg}", padding: 4px }
  menu-item-focus: { backgroundColor: "{colors.accent}", textColor: "{colors.accent-foreground}", rounded: "{rounded.md}" }
  dialog: { backgroundColor: "{colors.popover}", textColor: "{colors.popover-foreground}", rounded: "{rounded.xl}", padding: 16px }
  tooltip: { backgroundColor: "{colors.foreground}", textColor: "{colors.background}", typography: "{typography.caption}", rounded: "{rounded.md}", padding: 6px 12px }
  toast: { backgroundColor: "{colors.popover}", textColor: "{colors.popover-foreground}", rounded: "{rounded.lg}" }
  alert: { backgroundColor: "{colors.card}", textColor: "{colors.card-foreground}", rounded: "{rounded.lg}", padding: 8px 10px }
  alert-destructive: { backgroundColor: "{colors.card}", textColor: "{colors.destructive}", rounded: "{rounded.lg}", padding: 8px 10px }
  badge: { textColor: "{colors.foreground}", typography: "{typography.badge}", rounded: "{rounded.full}", height: 20px, padding: 2px 8px }
  badge-secondary: { backgroundColor: "{colors.secondary}", textColor: "{colors.secondary-foreground}", typography: "{typography.badge}", rounded: "{rounded.full}", height: 20px }
  status-success: { backgroundColor: "{colors.success}", rounded: "{rounded.full}", size: 8px }
  status-warning: { backgroundColor: "{colors.warning}", rounded: "{rounded.full}", size: 8px }
  status-destructive: { backgroundColor: "{colors.destructive}", rounded: "{rounded.full}", size: 8px }
  status-in-progress: { backgroundColor: "{colors.primary}", rounded: "{rounded.full}", size: 8px }
  status-neutral: { backgroundColor: "{colors.muted-foreground}", rounded: "{rounded.full}", size: 8px }
  empty-state: { textColor: "{colors.foreground}", rounded: "{rounded.lg}", padding: 64px 24px }
  skeleton: { backgroundColor: "{colors.muted}", rounded: "{rounded.md}" }
  code-block: { backgroundColor: "{colors.muted}", textColor: "{colors.foreground}", typography: "{typography.mono-caption}", rounded: "{rounded.md}", padding: 12px }
  separator: { backgroundColor: "{colors.border}", height: 1px }
  focus-ring: { backgroundColor: "{colors.ring}", width: 3px }
  chart-series-playback: { backgroundColor: "{colors.chart-playback}" }
  chart-series-imports: { backgroundColor: "{colors.chart-imports}" }
  chart-series-health: { backgroundColor: "{colors.primary}" }
  # Dark theme
  page-dark: { backgroundColor: "{colors.background-dark}", textColor: "{colors.foreground-dark}", typography: "{typography.body}" }
  page-description-dark: { backgroundColor: "{colors.background-dark}", textColor: "{colors.muted-foreground-dark}", typography: "{typography.body}" }
  sidebar-dark: { backgroundColor: "{colors.sidebar-dark}", textColor: "{colors.sidebar-foreground-dark}", width: 224px }
  sidebar-item-active-dark: { backgroundColor: "{colors.sidebar-accent-dark}", textColor: "{colors.sidebar-accent-foreground-dark}" }
  sidebar-item-active-icon-dark: { textColor: "{colors.sidebar-primary-dark}", size: 16px }
  sidebar-divider-dark: { backgroundColor: "{colors.sidebar-border-dark}", height: 1px }
  sidebar-focus-ring-dark: { backgroundColor: "{colors.sidebar-ring-dark}", width: 3px }
  settings-nav-item-active-dark: { backgroundColor: "{colors.accent-dark}", textColor: "{colors.accent-foreground-dark}" }
  panel-dark: { backgroundColor: "{colors.card-dark}", textColor: "{colors.card-foreground-dark}", rounded: "{rounded.lg}" }
  menu-dark: { backgroundColor: "{colors.popover-dark}", textColor: "{colors.popover-foreground-dark}", rounded: "{rounded.lg}", padding: 4px }
  button-primary-dark: { backgroundColor: "{colors.primary-dark}", textColor: "{colors.primary-foreground-dark}" }
  button-secondary-dark: { backgroundColor: "{colors.secondary-dark}", textColor: "{colors.secondary-foreground-dark}" }
  button-ghost-hover-dark: { backgroundColor: "{colors.muted-dark}", textColor: "{colors.foreground-dark}" }
  field-error-dark: { textColor: "{colors.destructive-dark}", typography: "{typography.caption}" }
  switch-off-dark: { backgroundColor: "{colors.input-dark}", rounded: "{rounded.full}" }
  tooltip-dark: { backgroundColor: "{colors.foreground-dark}", textColor: "{colors.background-dark}", typography: "{typography.caption}" }
  alert-destructive-dark: { backgroundColor: "{colors.card-dark}", textColor: "{colors.destructive-dark}" }
  status-success-dark: { backgroundColor: "{colors.success-dark}", rounded: "{rounded.full}", size: 8px }
  status-warning-dark: { backgroundColor: "{colors.warning-dark}", rounded: "{rounded.full}", size: 8px }
  status-destructive-dark: { backgroundColor: "{colors.destructive-dark}", rounded: "{rounded.full}", size: 8px }
  separator-dark: { backgroundColor: "{colors.border-dark}", height: 1px }
  focus-ring-dark: { backgroundColor: "{colors.ring-dark}", width: 3px }
  chart-series-playback-dark: { backgroundColor: "{colors.chart-playback-dark}" }
  chart-series-imports-dark: { backgroundColor: "{colors.chart-imports-dark}" }
  chart-series-health-dark: { backgroundColor: "{colors.primary-dark}" }
  # Stall causes (both themes)
  chart-series-stall-budget: { backgroundColor: "{colors.stall-budget}" }
  chart-series-stall-permit: { backgroundColor: "{colors.stall-permit}" }
  chart-series-stall-connection: { backgroundColor: "{colors.stall-connection}" }
  chart-series-stall-pipeline: { backgroundColor: "{colors.stall-pipeline}" }
  chart-series-stall-latency: { backgroundColor: "{colors.stall-latency}" }
  chart-series-stall-transfer: { backgroundColor: "{colors.stall-transfer}" }
  chart-series-stall-decode: { backgroundColor: "{colors.stall-decode}" }
  chart-series-stall-disk: { backgroundColor: "{colors.stall-disk}" }
  chart-series-stall-shared: { backgroundColor: "{colors.stall-shared}" }
  chart-series-stall-other: { backgroundColor: "{colors.stall-other}" }
---

# infinidysk design system

The design system for the web UI in `ui/`. Agents read it before adding or changing UI, and UI audits measure the code against it. The front matter holds the tokens ([DESIGN.md format](https://github.com/google-labs-code/design.md), version alpha); the sections say how to apply them.

**Precedence**

1. `AGENTS.md` project rules win: decimal units, *unowned* and *unlinked* wording, no media titles, every page usable on mobile.
2. Token values mirror `ui/src/index.css` (`:root` is light, `.dark` is dark) and the shadcn primitives in `ui/src/components/ui/`. Change a value here and in `index.css` in the same commit.
3. Roles, patterns, copy and accessibility rules follow the [Supabase Design System](https://supabase.com/design-system) used by Supabase Studio, translated to this stack. Names in parentheses, like (`FormItemLayout`), are the Supabase source; use the repo building block named next to them.
4. Values come from this file, not from Supabase. Studio uses 26px buttons, 24px page titles and 16px table cell padding; this UI uses the sizes below. Do not "correct" them toward Supabase.

**Stack.** React 19 and Vite, shadcn/ui (`radix-nova` style) on Tailwind CSS v4, Radix primitives, lucide-react, sonner, TanStack Query, React Router. There is no form library, chart library or Markdown renderer; the patterns below say what to use instead.

## Overview

infinidysk's UI is an operator console for a media backend. People open it to check that playback and imports are healthy, fix what is not, or change a setting, then leave. It should feel calm, technical and trustworthy: dense but never cramped, quiet until something needs attention.

The look follows Supabase Studio:

- **Quiet neutrals.** Blue-gray surfaces in both themes. Dark is near-black with the logo background's blue tint (`#070e1c`); light is near-white. The theme follows the OS unless the user picks Light or Dark in the account menu.
- **Structure from lines, not shadows.** 1px borders and rings separate surfaces; elevation is a small tonal step.
- **One accent.** infinidysk blue (the logo arrow, `#0194f7`) marks the main action, links, focus, selection and work in progress. Everything else is neutral; status colors only report state, and data colors only appear in charts.
- **Dense and technical.** 14px body text, 12px secondary text, 32px controls (28px in toolbars and tables), compact tables. Technical values (paths, hosts, keys, IDs, versions, logs) are set in Geist Mono; numbers use tabular figures.
- **Predictable structure.** A left sidebar shell; every page is a header followed by panels and tables; settings are a grouped side nav, a form column and a sticky save bar.

## Colors

The palette is the one already implemented in the app; keep it. It comes from the logo: the arrow's blue (`#0194f7`) is `primary`, its green (`#23a336`) is `success`, `chart-playback` sits near its teal (`#139c8f`), and dark surfaces take the logo background's navy tint (`#070e1c`). Neutrals are blue-gray (hue 260) in both themes.

### Roles

| Role | Token (Tailwind) | Use |
|---|---|---|
| Page | `background` (`bg-background`) | App background; sticky save bar at `/90` |
| Raised surface | `card` (`bg-card`) | Panels, cards, tables, metric tiles, alerts, option cards |
| Floating surface | `popover` (`bg-popover`) | Menus, select lists, dialogs, toasts |
| Tinted fill | `muted` (`bg-muted`, `bg-muted/50`) | Row hover, dialog and card footers, code blocks, skeletons, tracks |
| Neutral selection | `accent` (`bg-accent`) | Focused menu item, active settings nav item; `/60` for hover |
| Quiet fill | `secondary` (`bg-secondary`) | Secondary button and badge, a toolbar toggle that is on |
| Text | `foreground` | Primary text |
| Secondary text | `muted-foreground` | Descriptions, hints, metadata, table heads, placeholders, inactive nav, icons |
| Divider | `border` | Every 1px divider (`border`, `border-b`, `divide-y` default to it) |
| Control outline | `input` | Inputs, selects, tag inputs, outline toggles, switch off-track |
| Accent | `primary` | Main action, links, selection, in-progress state, health checks series |
| Focus | `ring` | Focus rings (`ring-ring/50`, `outline-ring/50`) |
| Status | `success`, `warning`, `destructive`; `success-text`, `warning-text` | Healthy or completed; degraded or needs a look; failed, blocking or destructive. The `-text` pair is for status-colored text |
| Data | `chart-playback`, `chart-imports`, `stall-*`, backbone hues | Chart series and categories (Data palettes) |
| Shell | `sidebar`, `sidebar-foreground`, `sidebar-accent`, `sidebar-accent-foreground`, `sidebar-primary`, `sidebar-border`, `sidebar-ring` | Sidebar and mobile top bar |

### Accent

(Supabase: [Color usage](https://supabase.com/design-system/docs/color-usage) › Primary and brand colors)

- `primary` is the only accent: the filled primary button; links and inline actions (`text-primary`, `variant="link"`); focus rings; selected state (`bg-primary/15 text-primary` on segments and chips, `border-primary ring-1 ring-primary` on option cards, the active nav icon); in-progress status; the health checks series.
- No second accent hue, no gradients, no colored section backgrounds or decorative tints.
- Hover is neutral (`hover:bg-muted`, `hover:bg-accent/60`), never a lighter blue.

### Status

| State | Token | Examples |
|---|---|---|
| Healthy, completed, live | `success` | Healthy, Completed, mounted, file open |
| Degraded, waiting, needs a look | `warning` | Degraded, Suspect, Waiting, stalls, update available |
| Failed, blocking, destructive | `destructive` | Failed, Needs action, Missing articles, errors |
| In progress | `primary` | Repairing, tracing |
| Neutral, idle, off | `muted-foreground` (dot at `/50`) | Unchecked, Queued, Paused, Ignored |

- A status color never stands alone: pair it with a label (`Status`) or an icon (Supabase Charts: status colors "always ship with an icon or label").
- Dots, icons, fills, borders and chart marks use `success` and `warning`; text uses `text-success-text` and `text-warning-text`, which are darker in light and the same as the base tokens in dark. `destructive` and `primary` pass as text and have no `-text` variant.
- Use colored text sparingly. Prefer a colored dot or icon beside neutral text; color the text itself only for short values that must stand out, such as an error count above zero.
- Don't tint the icons of destructive actions red; the confirmation dialog carries the destructive styling (Supabase Icons).

### Data palettes

Charts reuse the theme tokens, and two categorical sets have fixed colors of their own. Keep every assignment: a series or category has the same color on every chart, and a new one reuses an existing color before a new color is added.

| Series | Color |
|---|---|
| Playback (workload) | `chart-playback` |
| Health checks (workload), reads, cache hit ratio, provider requests | `primary` |
| Imports (workload), provider throughput | `chart-imports` |
| Stalls, missing articles | `warning` |
| Errors, failures, outages | `destructive` |
| Rescue reasons, in order | `warning`, `destructive`, `chart-imports`, `primary`, `muted-foreground` |

Stall causes on trace timelines (`causes` in `pages/traces.tsx`):

| Cause | Token | Value |
|---|---|---|
| Budget | `stall-budget` | `oklch(0.62 0.17 300)` |
| Permit | `stall-permit` | `oklch(0.74 0.15 75)` |
| Connection | `stall-connection` | `oklch(0.66 0.18 40)` |
| Pipeline | `stall-pipeline` | `oklch(0.66 0.11 190)` |
| Latency | `stall-latency` | `oklch(0.62 0.17 250)` |
| Transfer | `stall-transfer` | `oklch(0.7 0.13 220)` |
| Decode | `stall-decode` | `oklch(0.68 0.16 145)` |
| Disk | `stall-disk` | `oklch(0.64 0.12 110)` |
| Shared | `stall-shared` | `oklch(0.66 0.14 350)` |
| Other | `stall-other` | `oklch(0.6 0 0)` |

Backbone lanes in the provider priority editor (`pages/providers.tsx`): `oklch(0.66 0.15 h)` with `h = round(20 + i × 360 / n)`, where `i` is the backbone's position in the provider catalog and `n` the number of backbones in it, so a backbone keeps its color across visits.

- Stall and backbone colors are the same in both themes. On light cards several sit below 3:1, so they never carry meaning alone: each comes with a label or legend.

### Contrast

WCAG 2 ratios computed from the oklch values (sRGB):

| Token | Light on `background` | Dark on `background` | As text |
|---|---|---|---|
| `foreground` | 17.6:1 | 16.7:1 | Yes |
| `muted-foreground` | 5.4:1 (4.9:1 on `muted`) | 6.7:1 (5.8:1 on `muted`) | Yes |
| `primary` | 5.6:1 | 5.8:1 | Yes |
| `primary-foreground` on `primary` | 5.6:1 (white) | 5.8:1 (near-black) | Yes; 4.7:1 and 4.9:1 at the `/90` hover |
| `success` | 3.9:1 | 7.7:1 | No; use `success-text`. Icons, dots, borders |
| `warning` | 2.9:1 | 9.4:1 | No; use `warning-text`. Icons only beside a label in light |
| `success-text` | 5.5:1 (4.7:1 on its `/15` tint) | 7.7:1 | Yes |
| `warning-text` | 5.8:1 (5.3:1 on `muted`, 4.8:1 on its `/15` tint over `sidebar`) | 9.4:1 | Yes |
| `destructive` | 4.7:1 (4.0:1 on its `/10` tint) | 6.1:1 (4.8:1 on its `/20` tint) | Yes, except on its own tint in light |
| `chart-imports`, `chart-playback` on `card` | 4.2:1, 3.4:1 | 6.0:1, 7.8:1 | Chart marks |
| Stall causes, backbone hues on `card` | 2.4–4.0:1 | 4.6–7.8:1 | Chart marks, always labelled |

Targets: 4.5:1 for text; 3:1 for large text (24px, or 18.7px at weight 700), icons, control borders, focus rings and chart marks. The palette stays as implemented: meet the targets by choosing where each color appears (the "As text" column), not by adding or shifting colors. The exceptions: `success-text` and `warning-text` exist because light `success` and `warning` can't carry text; and for `primary`, light uses a deeper blue than the logo so white labels pass, while dark buttons carry near-black labels because no blue passes both as text on the dark page and under a white label. Filled `primary` hovers at `/90`; lower opacities drop the label below 4.5:1.

### Supabase vocabulary

Supabase docs and examples use their own class names. Translate them:

| Supabase | This repo |
|---|---|
| `bg-studio`, `bg` | `bg-background` |
| `bg-surface-75`, `bg-surface-100`, `bg-200` | `bg-card` |
| `bg-surface-200`, `bg-muted` | `bg-muted` |
| `bg-surface-300`, `bg-button` | `bg-secondary` |
| `bg-overlay`, `bg-dialog` | `bg-popover` |
| `bg-control`, `bg-selection` | `bg-accent` |
| `text-foreground` | `text-foreground` |
| `text-foreground-light`, `-lighter`, `-muted` | `text-muted-foreground` (there is no third text tier) |
| `border-default`, `border-muted`, `border-secondary` | `border-border` |
| `border-strong`, `border-control` | `border-input` |
| `brand`, `primary`, `primary-solid`, `primary-bright` | `primary` |
| `ring-ring`, `focus-ring` | `ring-ring/50` (built into primitives) |
| `--chart-1` … `--chart-8` | The series colors and data palettes (Data palettes) |

### Rules

- Colors come from this palette: the theme tokens in `ui/src/index.css` and the data palettes (`causes` in `pages/traces.tsx`, backbone hues in `pages/providers.tsx`). Components use token classes (`bg-card`, `text-muted-foreground`) and SVG charts `var(--color-*)`; no other hex, `rgb()`, `hsl()` or `oklch()` literals and no Tailwind palette classes (`text-green-600`, `bg-zinc-900`).
- Opacity modifiers on tokens are fine (`bg-primary/15`, `bg-muted/50`, `bg-foreground/10`, `text-sidebar-foreground/80`).
- Reuse an existing color before adding one. A new color is a CSS variable for both themes in `index.css`, mapped in `@theme inline`, and a token here.
- Components never branch on the theme; tokens resolve per theme (Supabase Charts). `dark:` variants belong in `components/ui/` primitives only.

## Typography

Fonts: **Geist Variable** for UI text and **Geist Mono Variable** for technical values, self-hosted through `@fontsource-variable` (fallbacks `ui-sans-serif, system-ui, sans-serif` and `ui-monospace, monospace`). `body` sets `text-sm antialiased`.

| Role | Token | Tailwind | Use | Supabase role |
|---|---|---|---|---|
| Page title | `page-title` | `text-xl font-medium tracking-tight` | `PageHeader` h1, sign-in and setup h1 | `heading-title` |
| Section title | `section-title` | `text-lg font-medium tracking-tight` | Settings category header h2 | `heading-section` |
| Dialog title | `dialog-title` | `text-base font-medium leading-none` | `DialogTitle`, `AlertDialogTitle`, `CardTitle` | `heading-subSection` |
| Panel title | `panel-title` | `font-medium` | Panel and chart headers, page section h2, item names | `heading-default` |
| Body | `body` | `text-sm` (inherited) | Everything else | `text-sm` |
| Label | `label` | `Label` | Form labels | — |
| Caption | `caption` | `text-xs` | Field hints, panel descriptions, row metadata, tooltips | `heading-compact` |
| Meta label | `meta-label` | `text-xs font-medium tracking-wide uppercase text-muted-foreground` | Nav group labels, sub-headings inside panels and dialogs, table heads | `heading-meta` |
| Numeric | `numeric` | `tabular-nums` | Counts, sizes, rates, durations, live values | — |
| Metric | `metric` | `font-mono text-lg font-medium tabular-nums` | Metric tile values | `MetricCard` value |
| Mono | `mono`, `mono-caption` | `font-mono`, `font-mono text-xs` | Paths, hosts, keys, IDs, versions, logs, raw errors | inline code |

- Two weights: 400 and 500 (`font-medium`). No semibold or bold text; emphasis uses `font-medium`.
- `tracking-tight` only on page and section titles.
- Supabase sets meta labels in mono; here they use Geist with `tracking-wide`. Write the source text in sentence case and let `uppercase` transform it.
- One h1 per page (from `PageHeader`), h2 for sections and panels, h3 for groups inside them. Pick the level by structure and the style by role.
- Minimum 12px (`text-xs`). Smaller arbitrary sizes (`text-[11px]`, `text-[0.65rem]`) only inside fixed-size chips (counters, avatar initials) and dense monospace visualizations such as trace timelines.
- Technical values in `font-mono`; inputs that hold them get `className="font-mono"`.
- `tabular-nums` wherever values update live or line up in columns; right-align them in tables.
- Long values: `truncate` names in tables and expose the full value on hover and focus; `break-words` or `break-all` for paths and error text; `text-pretty` for multi-line descriptions.

## Layout

### App shell

- From `lg` (1024px): a 224px sidebar (`w-56 bg-sidebar border-r`), sticky at full height. Top to bottom: the 48px brand row (`border-b px-4`; the wordmark is lowercase `infinidysk` with `dysk` in `text-primary`, as on the docs site), primary nav (scrolls when the window is short), and a footer (`border-t p-2`) with the live status block, the account menu and the version link.
- Below `lg`: a sticky 48px top bar (`bg-sidebar border-b z-30`) with the brand and a menu button; the menu opens inline below it with the same nav, status, account and version, and scrolls within the viewport.
- Main column: `mx-auto w-full max-w-6xl px-4 py-6 lg:px-8 lg:py-8`. App-wide banners (library conversion) render at the top of main; each page renders inside an error boundary keyed by route.

### Page anatomy

(Supabase: [Layout](https://supabase.com/design-system/docs/ui-patterns/layout), Page Header, Page Section)

1. **Back link**, detail routes only (`/system/traces/:id`): `← System` above the header (`mb-3 inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground`, `ArrowLeft size-3.5`). Top-level pages need no breadcrumbs; the sidebar shows where you are. Add breadcrumbs only if routes get deeper than two levels.
2. **`PageHeader`** (`ui/src/ui.tsx`): title, optional one-line description, page actions on the right, wrapping on narrow screens, `mb-6`. Skip the description when the page explains itself (Supabase: header meta is optional).
3. **Page notices**: `Alert`s right below the header (`mb-6`): restart required, queue paused, import report.
4. **Content** stacked with `space-y-6`. When a page has several sections, each gets a header: `h2` panel title, caption description, actions on the right (Supabase `PageSectionTitle`, `PageSectionDescription`, `PageSectionAside`).

### Width

Pick the width by content, not by page type (Supabase).

| Content | Width | Supabase |
|---|---|---|
| Settings categories | `max-w-3xl` (768px) | `small` (768px) |
| Focused forms | `max-w-xl` (576px) | `small` |
| Lists, tables, dashboards, detail pages | the shell, `max-w-6xl` (1152px) | `default` (1200px) |
| Dense logs and traces | the shell; scroll inside the panel | `full` |
| Sign-in and setup | `max-w-sm` (384px) | Interstitial (400px) |

### Settings

- Grid `md:grid-cols-[12rem_1fr] md:gap-10`; 24px gap when stacked.
- Side nav: groups (Platform, Automation, System) with meta-label headings (`px-2 pb-1`) and 20px between groups; items `h-8 rounded-md px-2`, active `bg-accent text-accent-foreground font-medium`, inactive `text-muted-foreground hover:bg-accent/60 hover:text-foreground`. Below `md` it becomes a full-width `Select` with the same groups.
- Each category renders a `Section`: header (`section-title` and description, `border-b pb-5 mb-6`), then its groups (`space-y-6`). A group is a `SettingsPanel`: an optional meta-label title, then a `bg-card` bordered panel with one field per row, rows split by 1px dividers and padded 16px. Short controls (inputs, selects, sliders) sit beside their label (`Field layout="row"`); long ones (lists, tag inputs, option cards, paths with previews) sit below it. Forms › Saving covers the save bar.
- The parent stays compact (Supabase): the page header says "Settings" in one line; each category carries its own descriptive header.

### Where actions go

(Supabase: put actions where the user is already looking)

| Situation | Place |
|---|---|
| Page-level actions (Add NZBs, Pause queue) | `PageHeader`, right side |
| A table or list with a toolbar (search, filters) | Right end of the toolbar, not the header |
| Section-level actions (Add provider) | Section header, right side |
| Row actions | Last table column, right-aligned |
| Form submit | Dialog footer or the settings save bar, right-aligned, primary last |
| Callout actions | Inside the `Alert`, below its description |

### Spacing

A 4px grid on the Tailwind scale; no arbitrary values such as `p-[13px]`.

| Step | px | Typical use |
|---|---|---|
| `1`, `1.5` | 4, 6 | Icon to text in chips; label to control (`space-y-1.5`) |
| `2` | 8 | Button groups, toolbars, row actions; table cell padding; sidebar padding |
| `3` | 12 | Header title to actions; metric tiles; filter panels |
| `4` | 16 | Between fields (`space-y-4`); dialog padding; mobile page gutter |
| `5` | 20 | Panels with a header; settings section header; between nav groups |
| `6` | 24 | Between page blocks (`space-y-6`, `mb-6`); mobile page top |
| `8` | 32 | Desktop page gutter and top |
| `10` | 40 | Settings nav to content |

## Elevation & Depth

Flat, like Studio: hierarchy comes from tonal steps and 1px lines, not shadows.

| Level | Treatment | Used for |
|---|---|---|
| Shell | `sidebar`, a step darker than the page in both themes | Sidebar, mobile top bar |
| Page | `background` | Page |
| Raised | `card` with a 1px `border` (or the primitives' `ring-1 ring-foreground/10`) | Panels, cards, tiles, alerts |
| Floating | `popover` with the ring; menus and select lists add `shadow-md` | Menus, selects, dialogs, toasts |
| Inset | `muted`, `muted/50` | Footers, code blocks, hover rows, tracks |

- No shadows on in-page surfaces.
- Dialog overlay: `bg-black/10` with `backdrop-blur-xs`. Sticky save bar: `bg-background/90 backdrop-blur` with `border-t`.
- One raised level inside a page: a panel may contain dividers, a meta-label heading or a `bg-muted/50` area, never another bordered panel. An `Alert` inside a panel or dialog may drop its border to sit flush (Supabase "sandwiched" admonition).
- Layers: shell `z-30`; dialogs, menus, tooltips and toasts `z-50`.

## Shapes

`--radius` is 6px; the scale derives from it in `index.css`.

| Token | Value | Class | Used for |
|---|---|---|---|
| `sm` | 3.6px | `rounded-sm` | Small inline targets (the `InfoTip` trigger) |
| `md` | 4.8px | `rounded-md` | Nav and menu items, tooltips, skeleton bars, code blocks, inner list boxes |
| `lg` | 6px | `rounded-lg` | Buttons, inputs, selects, toggles, panels, alerts, empty states, option cards |
| `xl` | 8.4px | `rounded-xl` | Cards, dialogs, alert dialogs |
| `full` | 9999px | `rounded-full` | Badges, counters, chips, status dots, switches, avatars, progress bars |

- Inner radius never exceeds outer radius; children touching a panel's edge rely on its `overflow-hidden`.
- Compact control sizes cap their radius inside the primitives; don't override it.
- No arbitrary radii outside `components/ui/`.

## Components

### Building blocks

Reuse these before writing new markup; a second hand-rolled copy of an existing block is an audit finding.

| Need | Supabase | This repo |
|---|---|---|
| Page title block | `PageHeader` | `PageHeader` (`ui/src/ui.tsx`) |
| Section with title and actions | `PageSection` | `SettingsSection` (`ui/src/ui.tsx`); the panel header below |
| Container for a table or list | `Card` + `Table` | `Panel` + `Table` |
| Grouped content | `Card` | `Card` (`components/ui/card.tsx`) |
| Metric | `MetricCard`, `ChartMetric` | Metric tiles (`Tiles` in `pages/overview.tsx`) |
| Form field | `FormItemLayout` | `Field` (stacked, `layout="row"` in settings panels, or `layout="horizontal"` in sheets), `Toggle` (switch row) |
| Callout | `Admonition` | `Alert` |
| Error block | `ErrorDisplay` | `Failure` |
| Loading | `ShimmeringLoader`, `Skeleton` | `Loading`, `Skeleton` |
| Empty state | `EmptyStatePresentational` | `Empty` |
| State label | Badge states, `StatusCode` | `Status`, `CheckStatus` |
| Metadata tag | `Badge` | `Badge` |
| Help icon | `InfoTooltip` | `InfoTip` |
| Confirmation | `AlertDialog`, `ConfirmationModal` | `ConfirmButton`, `ConfirmDialog` (controlled); `Dialog` for richer confirmations |
| Typed confirmation | `TextConfirmDialog` | `TextConfirmDialog` (`ui/src/ui.tsx`) |
| Table row actions | Row action buttons + overflow menu | `RowActions` (`ui/src/ui.tsx`): up to two inline icon buttons, the rest in a "More actions" menu with destructive items last |
| Side panel | `Sheet` | `EditorSheet` (`ui/src/ui.tsx`): the provider and Arr editors |
| Toast | Sonner | `toast` from `sonner`; `<Toaster position="bottom-right">` |
| Pagination | `Pagination` | `Pager` (`PAGE` = 50 rows) |
| Segmented control | `ToggleGroup` (segmented) | `ToggleGroup type="single"` |
| Copy and reveal a value | Data Input | `SecretField` (`pages/settings.tsx`) |
| Repeated values | `SingleValueFieldArray`, `MultiSelect` | `TextList`, `TagInput` (`pages/settings.tsx`) |
| Navigation | `Sidebar`, `InnerSideMenu` | `Nav` (`layout.tsx`), `CategoryNav` (`pages/settings.tsx`) |
| Skip link | `SkipToContent` | The first child of the shell (`layout.tsx`) |
| Browser title | Page titles | `useTitle`; `PageHeader` sets it from `title` or `tab` (`ui/src/ui.tsx`) |
| Formatting | — | `bytes`, `ago`, `stamp`, `day`, `duration` (`ui/src/format.ts`) |

### Buttons

(Supabase: [Button](https://supabase.com/design-system/docs/components/button))

| Variant | Supabase | Use |
|---|---|---|
| `default` (filled blue) | `primary` | The main positive action in view: Save changes, Add provider, Sign in. At most one visible at a time |
| `outline` | `default` | The workhorse: other page actions, toolbar buttons, Cancel, Discard |
| `ghost` | `text` | Row and panel icon actions, tertiary actions, menu triggers |
| `secondary` | — | A toolbar toggle that is on (Filters open) |
| `destructive` | `danger` | The confirm button of a destructive dialog; triggers stay `ghost` or `outline` |
| `link` | `link` | Inline actions in running text |

| Size | Height | Use |
|---|---|---|
| `default` | 32px | Form submits, dialog footers, empty-state actions, sign-in |
| `sm` | 28px | Page and section actions, toolbars, alert actions, pager |
| `xs` | 24px | Dense inline actions inside rows and tiles |
| `icon-sm` | 28px | Row and panel icon actions |
| `icon` | 32px | Icon buttons beside 32px inputs (sort order, refresh) |
| `icon-xs` | 24px | Icon actions inside dense rows |

- One filled primary per view (Supabase). An empty state or alert that repeats a page action uses `outline`.
- Labels are verb + object in sentence case (Copywriting); the icon goes first and matches the action (Icons).
- Secondary before primary; footers and toolbars right-aligned.
- Icon-only buttons need an `aria-label` (or `sr-only` text) and a tooltip with the same words.
- Pending: disabled, with `Loader2` spinning in place of the icon; when it takes more than a moment the label changes to the -ing form ("Importing…"). Keep the width steady.
- Unavailable for a reason the user can't see: Accessibility › Unavailable actions.
- Alternative ways to run the same action: a split button (the default choice on the button, alternatives in a chevron dropdown). Distinct actions: an overflow menu.
- A navigation link styled as a button: `Button asChild` around the router `Link`.

### Text inputs and selects

- `Input`: 32px, `rounded-lg`, `border-input`, transparent in light and `bg-input/30` in dark; 16px text below `md` so iOS doesn't zoom, 14px above. Focus `border-ring ring-3 ring-ring/50`; `aria-invalid` turns border and ring destructive.
- `Select`: same height and outline; the list is a `popover`; long lists group with `SelectGroup` and `SelectLabel`; filter selects start with an "Any …" option.
- Search: `Search` icon inside on the left (`absolute left-2.5 size-4 text-muted-foreground`), input `pl-8`, a short placeholder naming the target ("Search file names", "Filter"), and an `aria-label` when there is no visible label.
- Placeholders show an example value (`news.example.com`, `/media/tv`, `172.18.0.0/16`), never the label or an instruction.
- Units sit next to the value (`63 MB/s` in the label row) or in the hint; Supabase uses an input addon (`ms`, `seconds`).

### Choice controls

| Choice | Control |
|---|---|
| On or off, saved with the form | `Switch` in a `Toggle` row |
| One of 2–4 options that applies at once and is not saved (time window, tree or list, log level) | Segmented control: `ToggleGroup type="single"` |
| One of 2–3 options that need a sentence each | `OptionCards` (Radix `RadioGroup`) |
| A choice that runs a preview or conversion | Action cards, not a radio: the current option is a static card marked `Current`, each other is a button card ("Switch to …") |
| One of many, or long labels | `Select` |
| A value on a stepped scale | `Slider` with the value shown in the label row |
| A few free-form tokens | `TagInput` chips |

- Segmented control (Supabase Toggle Group › Segmented): never empty (ignore deselection), filters get an explicit "All", `aria-label` names the axis. One look app-wide: `variant="outline"`, selected item `bg-primary/15 text-primary`. Swapping whole panels is navigation or tabs; persisted settings use a switch or option cards.
- Option cards: `bg-card rounded-lg border p-3 text-left`, title `font-medium`, blurb `text-xs text-muted-foreground`, selected `border-primary ring-1 ring-primary`, hover `bg-accent/60`. One Tab stop with arrow keys between options (Accessibility).

### Panels and cards

- `Panel` (`bg-card rounded-lg border overflow-hidden`) holds tables, lists and logs edge to edge.
- A panel holding content gets `p-4`; one with a header gets `p-5` and a header row (`mb-4 flex flex-wrap items-start gap-x-6 gap-y-3`): title (`h2` or `h3`, `font-medium`) and caption description (`mt-0.5 text-xs text-muted-foreground`) on the left, controls on the right.
- Lists inside panels and dialogs: `divide-y rounded-md border`, rows `px-3 py-2`.
- `Card` (`rounded-xl ring-1 ring-foreground/10`, 16px spacing, `size="sm"` 12px) for grouped content outside tables; actions go in `CardFooter` (`border-t bg-muted/50`).

### Tables

(Supabase: [Table](https://supabase.com/design-system/docs/components/table); pattern rules under UI Patterns › Tables)

- `Table` inside a `Panel`. Head row 40px; head cells in the meta-label style (uppercase 12px `text-muted-foreground`), as Supabase sets `TableHead` in `heading-meta`. Body cells `p-2` and `whitespace-nowrap` by default; rows `border-b` with `hover:bg-muted/50`; no border after the last row.

### Status and badges

(Supabase: [Badge](https://supabase.com/design-system/docs/components/badge), Status Codes)

- `Status`: an 8px dot and a label (`gap-2`, no wrap), toned per Colors › Status. Extra detail follows a middle dot in muted text ("Failed · Replacement requested", `CheckStatus`). In table state columns pass `compact`: below `sm` the label drops to `text-xs` with a tighter gap but always shows, and the column head goes `sr-only`.
- `Badge` (`h-5 rounded-full text-xs font-medium`): static qualifiers of one or two words (Set by environment, Dry run, Backup, Custom, Removed). `outline` for neutral qualifiers, `secondary` for quiet categories, `destructive` for blocking problems (Quota used up). Badges stand out by design, so use them sparingly; never restyle them at the call site (case, radius, size) and never use them for live state (that is `Status`).
- Nav counters: `min-w-5 rounded-full px-1.5 text-xs leading-5 tabular-nums`; neutral `bg-foreground/10 text-muted-foreground`, needs action `bg-warning/15 text-warning-text`. The pill is `aria-hidden` and `sr-only` text inside the link states the meaning ("Health, 3 need action"); an `aria-label` on a plain `span` is not announced.

### Alerts

(Supabase: [Admonition](https://supabase.com/design-system/docs/fragments/admonition))

- `Alert` for page and section callouts: icon, optional `AlertTitle`, required `AlertDescription` (a title alone reads like an incomplete heading). Icons: `Info` notice, `TriangleAlert` warning, `CircleAlert` error, `CircleArrowUp` update.
- Variants: default for notices, `destructive` for errors (`Failure`). Warnings color the icon only (`TriangleAlert className="text-warning-text"`) and keep title and description in the default colors.
- Only `destructive` alerts carry `role="alert"`, so errors are announced and static notices do not interrupt a screen reader on page load.
- Actions are `size="sm"` buttons below the description. Their variant follows the action, not the alert: an encouraged action is `outline`, a destructive one `destructive`; a filled primary only inside a neutral alert, and rarely.

### Tooltips

- Tooltip: inverted (`bg-foreground text-background`), `text-xs`, `rounded-md px-3 py-1.5 max-w-xs`, no delay.
- `InfoTip`: an `Info` icon (`size-3.5 text-muted-foreground`) with an enlarged hit area that opens on hover, focus and tap. Content: one sentence that explains why (Copywriting).
- Icon-only buttons carry a tooltip with the same text as their `aria-label`.
- `title` attributes only repeat what is visible (a full timestamp, the full value of a truncated name). Anything users need must be reachable by keyboard and touch.

### Menus

- Dropdown and select content: `bg-popover rounded-lg ring-1 ring-foreground/10 shadow-md p-1`; items `rounded-md px-1.5 py-1 text-sm` with `bg-accent` on focus; destructive items `variant="destructive"`; labels and separators to group.
- Overflow menus for extra row or panel actions: a ghost `icon-sm` trigger with `Ellipsis` and an `aria-label` (Supabase Table › Multiple actions).
- The account menu holds the theme choice (System, Light, Dark) and Sign out.

### Dialogs

- `Dialog`: `bg-popover rounded-xl ring-1 ring-foreground/10 p-4`, close button top right. Header: title and description. Footer: `border-t bg-muted/50`, buttons right-aligned with the primary last; stacked below `sm` with the primary on top.
- Widths: `sm:max-w-sm` (default, confirmations); `sm:max-w-md` or `sm:max-w-lg` (short forms); `sm:max-w-2xl` or `sm:max-w-3xl` (previews, reports, lists); `sm:max-w-5xl` (player). The body scrolls within `100dvh - 2rem`.
- Which dialog to use: UI Patterns › Modality.

### Toasts

- sonner, bottom right, `popover` surface, an icon per type: `toast.success("Settings saved")`, `toast.error(error.message)`. When to use one: UI Patterns › Feedback and errors.

### Loading and progress

- `Loading` (three `h-9` skeleton bars, `aria-busy`) for a first load. Shape skeletons like what they replace (rows, tiles, chart height) so nothing jumps when data lands (Supabase Empty States: mind layout shift): Overview renders its cards with titles, tile-grid skeletons and a plot-height skeleton.
- Refetches keep the previous data on screen (`placeholderData: keepPreviousData`) and may dim it (`opacity-80`); never swap back to a skeleton.
- Spinners (`Loader2 animate-spin`) only in buttons and inline status, never full page.
- Progress: an `h-1.5 rounded-full bg-muted` track with a `bg-primary` fill, `role="progressbar"` with `aria-valuenow`, and the numbers as text beside it.
- Unknown totals and upgrade finalization omit the percentage and `aria-valuenow`; a one-third-width fill uses `motion-safe:animate-pulse` to show activity and stays still with reduced motion. A completed counted stage shows “Finishing…” instead of estimating time remaining.

### Icons

(Supabase: [Icons](https://supabase.com/design-system/docs/icons): paired, clear, consistent)

- lucide-react only. Primitives size them: `size-4` in buttons, menus and nav; `size-3.5` beside captions and in `sm` controls; `size-3` in `xs`.
- Icons accompany text; alone only in icon buttons with a label and tooltip.
- Tint like text: `text-muted-foreground` by default; status colors only for status.
- Decorative icons get `aria-hidden`; a standalone meaningful icon gets an `aria-label`.
- One icon per action everywhere:

| Action | Icon | Action | Icon |
|---|---|---|---|
| Add | `Plus` | Pause, resume | `Pause`, `Play` |
| Edit | `Pencil` | Back | `ArrowLeft` |
| Remove, delete | `Trash2` | Close, remove chip | `X` |
| Retry, reset | `RotateCcw` | More actions | `Ellipsis` |
| Refresh | `RefreshCw` | Filters | `SlidersHorizontal` |
| Download, upload | `Download`, `Upload` | Search | `Search` |
| Copy | `Copy` | Info | `Info` |
| Reveal | `Eye` | Warning | `TriangleAlert` |
| Generate key | `KeyRound` | Error | `CircleAlert` |
| Test connection | `PlugZap` | Pending | `Loader2` (spinning) |
| Speed test | `Gauge` | Drag handle | `GripVertical` |

### Provider priority editor

The signature control of the Providers settings (`AGENTS.md`: cascade routing is always on; priority levels are pools).

- Levels stack top to bottom ("Primary", "Fallback 1", …) with an "Articles still missing" arrow between them; providers in one level form a pool.
- Rows move between levels by drag and drop (`GripVertical` handle, whose tooltip carries the drag hint) and by keyboard; a new level can be added at the end.
- Row actions are Speed test and Edit plus an overflow menu (Reset usage when the provider has a quota, then Remove with confirmation); backbone lanes on the right show providers that share a backbone. A disabled provider shows an outline `Disabled` badge on a muted row.
- Each enabled row shows its connection `Status` after the name (Connected, Not connected yet, Paused, Can't connect), with the reason in `text-destructive text-xs` under the address when it can't connect. The backbone badge marks its lane color with a short vertical bar, never a dot, so it does not read as status.
- The provider sheet has Test connection under the password. Adding a provider or changing its login checks the login first; a failure stays in the sheet with "Add anyway" / "Save anyway".
- Auto-tune and Speed test open a dialog that explains what runs and the quota it uses before anything starts.

## UI Patterns

### Forms

(Supabase: [Forms](https://supabase.com/design-system/docs/ui-patterns/forms), [Form Item Layout](https://supabase.com/design-system/docs/fragments/form-item-layout), [Field](https://supabase.com/design-system/docs/components/field), [Data Input](https://supabase.com/design-system/docs/fragments/data-input))

**Field anatomy** (Supabase `FormItemLayout`: label, description, control, message)

- **Label**: `Label htmlFor` the control's `id`; a noun phrase naming the field ("Default category"), not an instruction.
- **Description**: `text-xs text-muted-foreground`; constraints or effects in the present tense ("Created if missing."); one or two short sentences, longer explanations in an `InfoTip`.
- **Error**: under the control in `text-xs text-destructive`, linked with `aria-describedby`; the control gets `aria-invalid`. Native constraints (`required`, `pattern`, `min`, `type`) catch the basics; server errors about a field go to that field.
- **Locked**: a value set by an environment variable renders disabled with a `Set by environment` outline badge after the label, the visible reason for the disabled control.

**Layout** (Supabase picks the layout by available width)

| Where | Layout | Shape |
|---|---|---|
| Page forms (settings) | `row` | Label and description left, control right in a 40% column from `md` up; label above control below `md` (`Field layout="row"`). `Toggle` rows already work this way |
| Wide dialogs and sheets | `horizontal` | Label and description in a third, control and error in two thirds from `sm` (`Field layout="horizontal"`) |
| Narrow dialogs, focused screens, columns under ~450px | `vertical` | Label above control, description below (`Field`) |

**Grouping**

- Related fields form a group: a bordered panel whose fields are rows split by 1px dividers (`SettingsPanel`; Supabase: one `CardContent` per field in a `Card`), or a meta-label heading followed by its fields.
- Optional and advanced fields sit behind a collapsible "Advanced settings" trigger in the meta-label style (`AdvancedSettings`; Supabase `CollapsibleCardSection`). In sheets, `FieldGroup` gives each group a meta-label heading.

**Saving**

- Save stays disabled until something changed; Discard appears only when there is something to discard.
- A settings category is one `<form id="settings">`. Its actions live in a sticky save bar at the bottom of the page that submits through `form="settings"` (Supabase: use a form ID when the submit button is outside the form). This replaces Supabase's per-card footer because a category saves as a whole. The bar shows while the form is dirty or the last save failed: the state on the left ("You have unsaved changes." or the save error, which also shows under the field it names through the error's `path`), then `Discard` (outline) and `Save changes` (primary).
- Pending: the submit button disables and shows a spinner; slow saves read "Saving changes…".
- Success: a short toast ("Settings saved"); the bar goes away.
- Failure: shown beside the actions with `role="alert"`, edits kept; never only a toast. Field errors stay on their fields.
- Leaving a dirty form asks first: in-app navigation through a blocker dialog, closing the tab through `beforeunload`. Use the discard copy below.
- Changes with side effects (converting library files, restarting) are not part of the draft. They run through their own preview or confirmation and wait until other unsaved changes are saved or discarded ("Save or discard your other changes first").
- Settings that need a restart say so in their hint, and a page banner offers the restart once saved.

**Forms in dialogs**

- Up to about five fields fit a `Dialog`. Longer forms, multi-section editors and detail views belong in a sheet from the right (`EditorSheet`, full width on phones; Supabase Modality). Its body scrolls between a fixed header and a footer with Cancel and the submit button; a save error sits just above the footer, so it stays in view.
- Enter submits; the primary button is last in the footer; a submission error renders inside the dialog above the footer.
- Dirty dismissal (Supabase): Escape, overlay click, the close button and Cancel all pass one guard. A clean form closes; a dirty one asks "Discard changes?" with "Keep editing" and "Discard changes". Cancel never discards silently.

**Repeated values** (Supabase field arrays)

- One value per row (`TextList`): an input per row with a ghost `Trash2` button (`aria-label="Remove path 2"`), an outline "Add …" button with `Plus` below, and a muted line when the list is empty.
- Short tokens such as categories: chips (`TagInput`). Enter, comma or space adds; Backspace on an empty input removes the last chip; each chip has a labelled remove button.
- Rows that mix controls (an input and a select) get a custom row with the same add and remove affordances. Validate rows in the form logic, not in the row; fully empty rows are drafts and are dropped on save.

**Secrets and copyable values** (Supabase Data Input)

- Stored secrets show masked and typing replaces them; Reveal (`Eye`) and Copy (`Copy`) are separate icon buttons.
- A freshly generated key says "Copy it now; it is hidden once saved."
- Copying confirms with "Copied". Values users copy (keys, URLs) sit in a read-only input, not plain text.
- Secret fields that aren't the user's own credentials opt out of password managers (`data-1p-ignore`, `data-lpignore="true"`, `data-form-type="other"`, `data-bwignore`); credential fields set `autoComplete`.

**Catalog choices**: a provider starts from a profile in the managed catalog, which fills host, port and backbone; a custom host is the escape hatch and may only pick an existing backbone (`AGENTS.md`).

### Tables

(Supabase: [Tables](https://supabase.com/design-system/docs/ui-patterns/tables), [Table](https://supabase.com/design-system/docs/components/table))

- A plain `Table` for short fixed lists. Lists with search, filters, sorting, paging or row actions follow the data table pattern: a toolbar, a `Table` in a `Panel`, and a `Pager`, with filtering and paging done by the server.
- **Toolbar** above the panel (`mb-3 flex flex-wrap items-center gap-2`): search first and growing (`relative min-w-56 flex-1`), then fixed-width selects, view toggles, icon buttons (sort order, refresh), and actions at the right end. Extra filters open below in a `bg-card rounded-lg border p-3` panel behind a Filters button (`secondary` while open). Changing a filter returns to page 1.
- **Columns**: identity (name), then state, numbers, time, actions. Heads are short nouns ("Name", "Status", "Size", "Added").
- Numbers right-aligned with `tabular-nums` and decimal units; times relative ("3 minutes ago") with the absolute time on hover; missing values "–".
- Fixed widths (`w-28`, `w-32`) on state, time, number and action columns so loading, empty and full states don't shift and stacked tables align. Names truncate at a max width.
- **Actions** are the last column, right-aligned and `whitespace-nowrap`; its head carries an `sr-only` "Actions" label. Row actions are ghost `icon-sm` buttons at least 8px apart; beyond two, show the most common and put the rest in an overflow menu. Destructive ones confirm (Modality).
- **Cross-links**: link the cell text (a completed import to its folder in Explorer), underlined on hover; don't make a row clickable when it also has actions.
- **Row navigation**: when opening the item is the only action, the whole row is the target: `tabIndex={0}`, Enter and Space open it, Ctrl or Cmd-click opens a new tab, focus shows an inset outline (Accessibility), and a trailing `ChevronRight` hints at it. Any other actions in such a row go into an overflow menu that stops event propagation.
- **Sorting**: sortable heads are buttons with an arrow (`ArrowUp`, `ArrowDown`, `ChevronsUpDown` when unsorted) (Supabase `TableHeadSort`). Lists without heads, such as the tree view, use a Sort select and an order button.
- **Narrow screens**: hide tertiary columns with `hidden sm:table-cell`, `md:table-cell` or `xl:table-cell` on both head and cells; keep name, state and actions. The name cell takes the slack (`w-full max-w-0` with a `truncate` name and the full name in `title`); secondary lines under a name or value may wrap (`whitespace-normal`), and badges beside a name wrap below it (`flex flex-wrap`). Fold a hidden column into a muted line under the name when it matters on phones. Wide tables also hide columns up to `xl` and `2xl` so they fit at 1280px. The panel scrolls sideways as a last resort.
- **Paging**: `Pager` below the panel, right-aligned ("Page N", Previous, Next as `outline sm`), hidden when everything fits; 50 rows per page, fetching one extra row to learn whether there is a next page.
- **Feeds** on dashboards (Overview › Recent reads) show 10 rows; below the panel, right-aligned, a muted "10 of N", a ghost Show fewer once expanded, and an `outline sm` Show more that adds 10. System › Logs works the same in steps of 50, and shows only the time below `sm` (full timestamp on hover).

### Empty states

(Supabase: [Empty States](https://supabase.com/design-system/docs/ui-patterns/empty-states), [Empty State Presentational](https://supabase.com/design-system/docs/fragments/empty-state-presentational))

| Case | Presentation | Example |
|---|---|---|
| Nothing set up yet, and the user can act | `Empty`: dashed border, title, one-line description, and the action as an `outline` button so the header keeps the only primary | "No providers yet" · "Add a Usenet provider to start importing and streaming." · Add provider |
| Nothing yet, and data arrives on its own (queue, health checks, logs, traces) | The same frame as the full view: table heads dimmed, one row with a title and when data will appear | "No health checks yet" · "Imported files are checked over time." |
| No results for a search or filter | The table stays; one row: "No results found", what was searched, and how to widen it (Clear filters). No hover on that row (`[&>td]:hover:bg-inherit`) | "No files match “…”" |
| A deep link to something that no longer exists | A centered `Alert` (`max-w-md`): title, description, a button back to the parent | "Unable to find trace" · Back to System |

- Say what is missing, then how to get it, and include the action (Copywriting).
- Empty states replace content in place and keep the page header and its actions, so loading, empty and full views don't shift.

### Modality

(Supabase: [Modality](https://supabase.com/design-system/docs/ui-patterns/modality), [Alert Dialog](https://supabase.com/design-system/docs/components/alert-dialog), [Text Confirm Dialog](https://supabase.com/design-system/docs/fragments/text-confirm-dialog), [Confirmation Modal](https://supabase.com/design-system/docs/fragments/confirmation-modal))

Modals interrupt, so use them sparingly: to confirm, to collect input an action needs, or to slow a destructive action down.

| Need | Use |
|---|---|
| Confirm a critical or destructive action that fits one short paragraph | `AlertDialog` (`ConfirmButton`) |
| Irreversible and broad: deleting library files, bulk deletes | Typed confirmation: an `AlertDialog` whose confirm button stays disabled until the exact name or count is typed (Supabase `TextConfirmDialog`) |
| A confirmation that needs context: affected items, a dry-run result, a callout | `Dialog` with a body and footer (Supabase `ConfirmationModal`) |
| A short form, picker or report | `Dialog` |
| A long form, editor or detail view | A sheet from the right |
| Information that needs no decision | Inline `Alert` or a toast, not a modal |

- Repeat the verb: the title and confirm button echo the button that opened the dialog (Remove → "Remove this provider?" → Remove).
- Keep it simple: no subtitles or extra callouts unless needed.
- `AlertDialog`: clicking outside does nothing; Escape and Cancel back out; the description is one paragraph without lists or inputs (otherwise use `Dialog`). Destructive confirms use `variant="destructive"`; Cancel is always there.
- When the action can fail and the user should retry from the dialog, keep it open with a pending confirm button and show the error inside (Supabase async Alert Dialog). Otherwise close it and report with a toast.
- Forms in dialogs follow Forms › Forms in dialogs.
- Don't stack modals, except a confirmation over the form that opened it. Use the smallest width that fits (Components › Dialogs).

### Navigation

(Supabase: [Navigation](https://supabase.com/design-system/docs/ui-patterns/navigation), [Skip to Content](https://supabase.com/design-system/docs/fragments/skip-to-content))

- Primary nav: the sidebar lists the pages in a fixed order (Overview, Queue, Explorer, Health, Stats, System, Settings), icon and label, one route each. Overview answers "is it working right now" (what needs attention, live reads, recent activity); Stats holds the per-provider, per-workload and per-app breakdowns. Active `bg-sidebar-accent text-sidebar-accent-foreground` with a `text-primary` icon; inactive `text-sidebar-foreground/80`, hover `bg-sidebar-accent/60`. Page names are title case and one or two words.
- Counters beside nav items show work waiting for the user (jobs in the queue, files that need action). An item can carry a neutral and a needs-action counter side by side (Queue: jobs, then downloads that need you in the Arr).
- Overview opens with a needs-attention list when something does (files that need action, downloads that need you in an Arr, providers with an open circuit, a paused queue, the mount down): one row per problem with a `Status` and a link to where it is fixed. It renders nothing when all is well.
- Overview activity plots average download speed per bucket as bars stacked by workload, Playback at the bottom. The header shows what was downloaded and served, each with its fastest 2 s rate; the legend above the plot gives each workload's total and toggles it. Errors draw in a lane under the bars.
- The live status block (files open, read rate, connections) links to Overview; the version block links to System, which holds the version card and the support pack, and turns `warning` when an update is available.
- Sub-navigation changes the URL (Supabase NavMenu): settings categories are `/settings/:category`; search and filters worth sharing live in the query string (`/explorer?search=`). Segmented controls only switch view state.
- Detail routes start with a back link (Layout › Page anatomy).
- Browser titles (Supabase page titles): most specific first, separated by ` | `, ending in `infinidysk`: `Queue | infinidysk`, `Providers | Settings | infinidysk`, `<file name> | Trace | System | infinidysk`. Set them in one shared helper, never ad hoc.
- Skip link: the first focusable element is "Skip to content", hidden until focused, pointing at `<main id="main" tabIndex={-1}>`; the sidebar stays outside `main`. `main` is a focus target, not a control, so it carries `outline-none`.
- Mobile: the top bar's menu button sets `aria-expanded`; the menu closes on navigation.

### Feedback and errors

(Supabase: Forms › Error handling, [Sonner](https://supabase.com/design-system/docs/components/sonner) › When to use, Connect Interstitials › Action feedback)

| Scope | Feedback |
|---|---|
| One field | Error under the field |
| A failed submission whose form or dialog is still open | Message next to the actions, `role="alert"`, input kept |
| A section or page that is blocked or changed state | `Alert` or `Failure` in place |
| A page that crashed | The page error boundary: destructive `Alert` with Reload |
| An action that finished or failed after its surface closed | Toast |
| A long-running job (conversion, maintenance, health check) | Status and progress in place, no toast per step |

- A toast is never the only feedback for something the user must fix.
- Clear a stale error when the user retries or changes what it was about.
- Error text says what failed and what to do (Copywriting). For technical server errors, show a plain summary first and the raw message below in `font-mono text-xs` with `break-all` or `whitespace-pre-wrap`.

### Charts and metrics

(Supabase: [Charts](https://supabase.com/design-system/docs/ui-patterns/charts), [Metric Card](https://supabase.com/design-system/docs/fragments/metric-card))

- Anatomy (Supabase `Chart`): a panel with a header (title, optional `InfoTip`, actions or the time-window control), optional headline metrics, the plot, and an optional footer table.
- Metric tiles: a caption label, the value in `metric`, an optional hint and sparkline. Tiles share 1px dividers (`grid gap-px bg-border rounded-lg border`, cells `bg-card p-3`): 2 columns on mobile up to 4–6, and the last tile stretches so no row ends in an empty cell. Inside a panel, pass `flush`: the grid drops its own border and radius and keeps the dividers (Elevation: one raised level).
- A headline rate shows the number the user feels, and its label says what it counts: Overview's Provider misses counts playback requests only, since health checks miss by design. It counts provider attempts, not what the viewer saw, so its InfoTip says another provider may still fetch the article.
- Plots are hand-drawn SVG, `aria-hidden`, with the same numbers available as text (tiles or a table). No axes by default (Supabase: grid and Y axis off); faint gridlines in `border`, and at most one scale label, the value of the top gridline. Hover shows a tooltip with the exact value and time, kept inside the plot: line charts add a vertical guide in `muted`, bar charts dim the other bars.
- The data picks the chart: amounts per time bucket (bytes, requests, reads, errors) draw as bars; ratios and gauges (cache hits, missing share) draw as lines or areas.
- Stacked bars use a linear scale from zero, never a log scale: on a log scale a segment's height depends on what sits under it. Hiding a series in the legend rescales the plot to the rest, which is how small series are inspected.
- Header metrics summarize the whole window in the plot's unit (Supabase `ChartMetric`) and stay put when a series is hidden. Per-series totals sit in the legend above the plot, which also toggles the series.
- A problem count beside a plot in another unit (errors beside bandwidth) draws in its own lane under the plot, on its own scale, never on the plot's scale.
- The bucket still filling draws at half opacity; its rates cover the time it has run, and its tooltip says "so far".
- A window draws at most about 120 bars, so bars stay at least 2 px wide on a phone: all-time history switches from days to weeks past 120 days.
- Series and category colors follow Colors › Data palettes; a series keeps its color on every chart (Supabase).
- Status colors only for series that measure a problem (errors, stalls, missing articles, outages), never for neutral metrics; amber on a neutral metric reads as a problem (Supabase).
- Categories (stall causes, backbones, rescue reasons) always come with a labelled legend; their colors never carry meaning alone.
- Loading, empty and disabled states fill the plot area at its normal height: a skeleton, "No data for this window", or an overlay with the reason and action (Supabase `ChartContent`).
- The time window is a segmented control in the section header with an explicit "All".

### Code, logs and long text

(Supabase: [Markdown](https://supabase.com/design-system/docs/ui-patterns/markdown), [Code Block](https://supabase.com/design-system/docs/fragments/code-block), [Typography](https://supabase.com/design-system/docs/typography) › Inline code)

- Single technical values in text or tables: `font-mono`.
- Blocks (reports, raw errors, support output): `pre` with `bg-muted/50 rounded-md border p-3 font-mono text-xs whitespace-pre-wrap`, scrolling inside (`max-h-[60vh] overflow-auto`). Offer Copy or Download when users will paste it elsewhere; confirm only after the clipboard write succeeds.
- Commands users run must work as shown: no pseudo-code, no `$` prompt.
- Logs: `bg-card divide-y rounded-lg border font-mono text-xs`, one entry per row (`px-3 py-1.5`): time (muted, tabular), level (uppercase; `warning` for warnings, `destructive` for errors), message, muted context. A filter input and a level segmented control sit above.
- Markdown isn't rendered today; if it is needed, add one shared renderer with GitHub-flavored defaults.
- Release notes (System › version card › View release notes): the server parses `CHANGELOG.md` into releases, so no markdown renderer is involved. A `sm:max-w-2xl` dialog lists them newest first: the version in mono as an `h3` linking to its GitHub compare view, an outline `Installed` badge on the running one and the date as a caption; each section under a meta-label heading (breaking changes add a `TriangleAlert` in `text-warning-text`); changes as a bulleted list with muted markers, the scope as a muted mono caption before the text, and issue and commit links after it as muted mono captions.

### Focused screens

(Supabase: [Connect Interstitials](https://supabase.com/design-system/docs/ui-patterns/connect-interstitials))

Sign-in, first-run setup and the upgrade-failed screen sit outside the app shell.

- Centered (`grid min-h-screen place-items-center p-4`): the 48px mark (`/mark.svg`, the logo without its navy tile) above the title (`page-title`) and a short description, then one card (`bg-card rounded-lg border p-5`), `max-w-sm` for forms and wider only to show a report (`max-w-2xl`).
- The tab title names the screen ("Sign in | infinidysk", "Upgrade failed | infinidysk") through `useTitle` and is restored when it closes.
- One full-width primary action ("Sign in", "Create account", "Retry the import"); its pending label matches the verb ("Signing in…").
- With SSO set up, the sign-in card offers **Sign in with SSO**, a link to `/auth/oidc/login`. Under the password form it is an `outline` full-width button below an "or" divider; without the password form it is the primary action. An SSO failure shows in the card like any other failed sign-in.
- The title stays the same across states; loading, error and success render inside the same card.
- Field errors under the field; a failed submission below the action with the input kept, focus returned to the field to fix, and API errors rewritten as "what failed, then the fix"; a blocked state as an `Alert` in the card.
- Username fields turn off autocapitalize, autocorrect and spellcheck; usernames are case-sensitive.

## Copywriting

(Supabase: [Copywriting](https://supabase.com/design-system/docs/copywriting))

Voice: direct (say what it does), action-oriented (what happens, not what we built), technical without jargon, pragmatic about limits and tradeoffs.

| Element | Do | Don't |
|---|---|---|
| Buttons | Verb + object: "Add provider", "Remove from queue", "Save changes" | "Submit", "OK", "Yes", "Configure" |
| Secondary button | "Cancel" | "Go back", "No" |
| Labels | Name the field: "Default category" | "Choose your default category" |
| Hints | Constraints and effects, present tense: "Letters, numbers and dashes only." | Concepts or future tense: "This will limit…" |
| Nav and page titles | Title case, one or two words: "Overview" | |
| Headings, section labels, buttons, menus | Sentence case: "Health monitoring", "Arr instances" | "Health Monitoring" |
| Page descriptions (`PageHeader`, settings categories) | A declarative fragment without a trailing period: "NZBs importing now, then finished imports, newest first" | Instructions: "Use this page to…" |
| Headings | Name the thing: "Providers" | "Manage your providers" |
| Errors | What failed, then the fix: "Connection failed: timeout after 30 seconds. Check the host and port." | "Something went wrong", "Oops", "Sorry" |
| Success | What happened, briefly: "Settings saved", "Removed" | "Success!", "Operation completed successfully" |
| Tooltips | Why, in one sentence | What the control is: "This is a switch" |
| Empty states | What's missing + how to add it: "No providers yet. Add a Usenet provider to start importing and streaming." | "You don't have any providers" |
| Loading | What's happening, matching the verb: "Saving changes…", "Importing…" | "Please wait…", "Loading…" |
| Confirmations | Question + consequence in active voice: "Remove this provider?" / "Imports and playback stop using it right away." | "Are you sure?" |

- Avoid marketing words (easily, simply, just, powerful, seamless), vague verbs (manage, handle), "please", "sorry", "oops" and exclamation marks.
- No italics; bold rarely; technical terms in mono.
- Terms:
  - **infinidysk** is always lowercase, even at the start of a sentence.
  - **Arr** and **Arrs** for Sonarr, Radarr, Lidarr, Chaptarr, Whisparr and the rest; "Arr instance".
  - **SABnzbd**, **WebDAV**, **NZB**/**NZBs**, **STRM file**, **PAR2**, **yEnc**, **Usenet**, provider, backbone, mount, library.
  - **Unowned** (no Arr imported it) and **unlinked** (no symlink or STRM file points to it and it's no longer in the download history). Never "orphan".
  - **Sign in**, **Sign out** in the UI; "login" only in code and routes.
- Numbers and units:
  - Decimal units: KB, MB, GB and TB are 10³ to 10¹² bytes; rates in MB/s; never MiB, GiB or Mbit (`AGENTS.md`). A space before the unit ("1.4 TB", "63 MB/s"), none before % ("80%").
  - Format with `ui/src/format.ts`: `bytes()` sizes, `ago()` relative times, `stamp()` full timestamps (tooltips), `day()` dates, `duration()` durations. Counts with `toLocaleString()`; plurals spelled out ("1 file open", "3 jobs").
  - Missing value "–" (en dash); truncation and pending labels "…" (one character); join details with " · "; curly quotes (“ ” ’).
- Never write media titles in examples, placeholders, fixtures or docs (use `<release-name>`), and never name competing projects (`AGENTS.md`).

## Accessibility

(Supabase: [Accessibility](https://supabase.com/design-system/docs/accessibility))

Before shipping: every interactive element is keyboard-focusable; unavailable actions are discoverable and explained; everything is announced by a screen reader; text is legible and scales; it works on a small, older phone.

**Focus**

- Native elements first (`button`, `a href`, inputs). `tabIndex={0}` only on custom interactive elements, with Enter and Space handlers.
- One focus style, tinted by `ring`: controls use the primitives' `focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50`; everything else inherits `outline-ring/50` from the base layer. Variants never change the focus color.
- `:focus-visible`, not `:focus`. Never `outline-none` or `outline-hidden` without a visible replacement.
- Interactive table rows use an inset outline (`focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-ring`), never `ring-*`: box-shadow doesn't reliably paint on `<tr>`, notably in Safari.
- Don't animate focus indicators; prefer `transition-colors` to `transition-all` on custom focusable elements.
- Everything the mouse can do, the keyboard can: drag and drop has keyboard sensors; row links honor Ctrl and Cmd-click.

**Groups and skipping**

- Radio-like groups (option cards, segmented controls) are one Tab stop; arrow keys move between options and Space selects. Radix `ToggleGroup` and `RadioGroup` do this; hand-built `role="radio"` buttons must too.
- Long lists offer search, filters or paging; the shell gets a skip link (Navigation).

**Unavailable actions**

- Native `disabled` removes a control from the Tab order. When the reason isn't obvious, keep it focusable (`aria-disabled="true"`, guard the handler) and explain it in a tooltip (Supabase `focusableWhenDisabled`). The shadcn `Button` sets `disabled:pointer-events-none`, so a natively disabled button can't show a tooltip.
- Significant restrictions also get visible page context: an `Alert` or the `Set by environment` badge.

**Screen readers**

- `alt` describes image content; decorative images (the logo beside the product name) use `alt=""`.
- Meaningful standalone icons get `aria-label`, decorative ones `aria-hidden`; never hide a focusable element.
- Icon-only buttons and columns without a visible head get an `aria-label` or `sr-only` text.
- Visual-only values need a text equivalent: counters `sr-only` text, progress bars `role="progressbar"` with `aria-valuenow`, charts the same numbers as text nearby.
- Errors that appear after an action use `role="alert"` (a `destructive` `Alert` already does).
- `Field` links its hint and error to the control inside it (`aria-describedby`, `aria-invalid`) through `FieldContext`; `Input`, `SelectTrigger`, `Switch` and `Slider` read it.

**Contrast, text and motion**

- 4.5:1 for text; 3:1 for large text, icons, control borders, focus rings and chart marks (Colors › Contrast).
- Sizes in rem; the layout holds at 200% zoom; no fixed-height boxes that clip text.
- Motion stays short (the primitives' 100ms fades and zooms); wrap anything larger in `motion-safe:`. Spinners only for pending work.

**Touch targets**

- At least 24×24px. Extend compact targets with an invisible pseudo-element (`relative after:absolute after:-inset-2`, as `InfoTip` and `Switch` do; Supabase `hit-area`) and keep 8px between neighbours so the areas don't overlap.

## Responsive Behavior

- Breakpoints are Tailwind's defaults: `sm` 640px, `md` 768px, `lg` 1024px, `xl` 1280px; classes are mobile-first.
- Every page works at 360px wide (`AGENTS.md`: usable on mobile). Check 360px, 768px and 1280px in both themes.

| Element (breakpoint) | Below | From |
|---|---|---|
| App shell (`lg`) | Sticky top bar with an inline menu | 224px sidebar |
| Page gutter (`lg`) | 16px sides, 24px top | 32px sides and top |
| Settings nav (`md`) | `Select` with grouped options | Grouped side nav, 192px |
| Page form rows (`md`) | Label above control | Label left, control right |
| Dialog footer (`sm`) | Stacked, primary on top | One row, right-aligned |
| Metric tiles (`sm`, `lg`) | 2 columns | 3–6 columns |
| Tables (`sm` to `2xl`) | Tertiary columns hidden | All columns |

- Page headers and toolbars wrap (`flex-wrap`); search inputs grow to fill the row.
- No page-level horizontal scrolling; wide content scrolls inside its panel.
- Dialogs: full width minus 32px, at most `100dvh - 2rem` tall, body scrolls.
- No hover-only affordances: row actions stay visible; tooltips that carry information open on tap (`InfoTip`).
- Only the shell and the settings save bar are sticky.

## Do's and Don'ts

**Do**

- Reuse the building blocks and semantic tokens.
- Keep one filled primary button per view; reach for `outline` first.
- Pair status colors with a label or icon.
- Right-align numbers with `tabular-nums` and decimal units.
- Put actions where the user is already looking.
- Confirm destructive actions; ask for typed confirmation before irreversible bulk deletes.
- Keep the user's input when something fails and show the error next to the actions.
- Check both themes and a 360px viewport.

**Don't**

- Add colors outside the palette (theme tokens and data palettes) or use Tailwind palette classes.
- Add shadows, gradients, colored backgrounds or a second accent for emphasis.
- Use status colors on neutral metrics or as the only signal.
- Make a toast the only feedback for something the user must fix.
- Hide information in hover-only states or `title` attributes.
- Use title case on buttons and labels, exclamation marks, "please", "orphan", binary units or media titles.
- Nest bordered panels, or use arbitrary radii, spacing or text sizes outside the listed exceptions.
- Restyle primitives at the call site (badge case or radius, button heights).
- Open a modal for information that needs no decision, or stack modals.

## Audit Checklist

Report one finding per issue with `file:line`, the rule (section › heading), a severity and a suggested fix, grouped by page. Severity: **high** for accessibility failures, data-loss risks or a broken pattern on a main flow; **medium** for pattern or token violations; **low** for copy and visual drift. Search hints are regular expressions over `ui/src`.

**Tokens and color**

- [ ] Tokens here match `ui/src/index.css` in both themes and the data palettes in `pages/traces.tsx` and `pages/providers.tsx`.
- [ ] No color literals outside `index.css` and those two data palettes: `#[0-9a-fA-F]{3,8}\b|rgba?\(|hsla?\(|oklch\(`.
- [ ] No Tailwind palette colors: `\b(text|bg|border|fill|stroke|ring|outline|from|via|to)-(red|orange|amber|yellow|lime|green|emerald|teal|cyan|sky|blue|indigo|violet|purple|fuchsia|pink|rose|slate|gray|zinc|neutral|stone)-\d{2,3}\b`.
- [ ] `dark:` only under `components/ui/`.
- [ ] Status colors always come with a label or icon; colored text only where Colors › Contrast allows it.
- [ ] Charts follow Colors › Data palettes; status colors only on problem series; categories labelled.

**Typography**

- [ ] One h1 per page through `PageHeader`; heading levels in order.
- [ ] Roles from the scale; `text-\[` only where allowed; no `font-(semibold|bold|extrabold|black|light|thin)`.
- [ ] Technical values in mono; numbers `tabular-nums` and right-aligned in tables.

**Layout**

- [ ] Back link (detail routes) → `PageHeader` → notices → content; widths per Layout › Width; actions per Layout › Where actions go.
- [ ] No arbitrary spacing outside `components/ui/`: `\b(p|m|gap|space-[xy])[trblxy]?-\[`.
- [ ] Settings: grouped side nav (`Select` below `md`), category sections, one form, sticky save bar.

**Components and patterns**

- [ ] Building blocks reused, not re-implemented.
- [ ] One filled primary per view; variants and sizes per Components › Buttons; icon-only buttons labelled and with a tooltip.
- [ ] `Status` for live state, `Badge` for one- or two-word qualifiers, neither restyled.
- [ ] Forms: labels, hints, inline errors, layout by width, dirty state, save bar, discard confirmation, pending labels, no toast-only errors.
- [ ] Tables: in a `Panel`, column order, fixed widths, `sr-only` actions head, overflow menu past two actions, zero-results row, `Pager`, responsive columns.
- [ ] Empty states: the right case and copy.
- [ ] Modality: the right dialog for the job; typed confirmation for irreversible bulk deletes; titles and buttons repeat the verb.
- [ ] Navigation: a URL per view, browser titles, skip link, back links on detail routes.
- [ ] Feedback matches its scope.

**Copy**

- [ ] Sentence case everywhere except page names in navigation; verbs on buttons; page descriptions without a trailing period.
- [ ] None of: `\b(Please|please|Sorry|Oops|successfully)\b`, `!"` in UI strings, `(?i)orphan`, `\b(KiB|MiB|GiB|TiB|Mbps|Mbit|Gbit)\b`, "Log in" or "Logout" in UI copy.
- [ ] Terms, numbers and formats per Copywriting.

**Accessibility**

- [ ] Visible focus everywhere; `outline-none|outline-hidden` outside `components/ui/` only with a replacement.
- [ ] Icon-only buttons (`size="icon…"`) and head-less columns labelled.
- [ ] Custom radio groups take arrow keys; skip link present; disabled controls with a hidden reason stay focusable.
- [ ] Contrast per Colors › Contrast; touch targets at least 24px.

**Responsive**

- [ ] At 360px: no page-level horizontal scroll; tables, toolbars and dialogs usable. At 768px and 1280px: layout per Responsive Behavior.
