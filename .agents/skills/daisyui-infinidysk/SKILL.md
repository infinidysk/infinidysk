---
name: daisyui-infinidysk
description: InfiniDysk frontend UI conventions for daisyUI 5 + Tailwind 4 on the `night` theme. Use when building, reviewing, auditing, or restyling any page, component, form, table, modal, or settings screen under frontend/app.
---

# daisyUI best practice for InfiniDysk

The InfiniDysk admin UI uses **daisyUI 5**, **Tailwind CSS 4**, and the **`night`** theme
(`<html data-theme="night">`). Always match these conventions instead of inventing new
styling. For component syntax, consult the official reference:
<https://daisyui.com/llms.txt> (or `https://daisyui.com/components/<name>/`).

## 1. Reuse before writing

Climb this ladder and stop at the first rung that works:

1. A UI kit wrapper in `frontend/app/components/ui/`
2. A plain daisyUI component class
3. Tailwind utilities on top of daisyUI
4. A CSS module (`*.module.css` + matching `*.module.css.d.ts`), **only** for
   layout that daisyUI cannot express (CSS grids, sticky columns, virtualised rows)

| Need | Use |
|------|-----|
| Page title, subtitle, header actions | `PageHeader({ title, subtitle, actions })` |
| Buttons | `Button` — variants `primary` `success` `danger` `warning` `secondary` `outline` `ghost`; sizes `xsmall` `small` (default) `medium` `large` `rounded` |
| Form fields | `Field`, `Label`, `HelpText`, `Input`, `InputGroup`, `Select`, `Textarea`, `Checkbox`, `Toggle`, `Check` |
| Status and feedback | `Alert` (`info` `success` `warning` `danger`), `Badge`, `Spinner`, `Tooltip` |
| Dialogs | `Modal` |
| Segmented filters | `RadioJoinFilter`, `Tabs` / `TabPanel` |
| Settings screens | `SettingsPage`, `SettingsSection`, `SettingsCard`, `SettingsPanel`, `SettingsIntro`, `ManagedSetting` |
| Icons | `Icon name="…"` (Material Symbols Rounded; `filled` for active/selected state) |

## 2. Page shell

Every top-level route uses the same shell so spacing matches its siblings:

```tsx
<section className="flex min-h-full min-w-0 flex-col gap-4 px-4 py-4 text-sm md:px-8">
  <PageHeader title="…" subtitle="…" actions={…} />
  …
</section>
```

- One `h1` per page, which `PageHeader` provides. Do not hand-roll `text-xl` titles.
- Empty, not-found, and error states get a `card` with an icon, a heading, one sentence
  of explanation, and a recovery action. Never leave a bare `<p>` plus a bare link.

## 3. Colour

- Use semantic tokens only: `base-100/200/300`, `base-content`, `primary`, `secondary`,
  `accent`, `info`, `success`, `warning`, `error`, plus their `*-content` pairs.
- Show muted text with opacity on `base-content` (`text-base-content/60`) and borders
  with `border-base-content/10`.
- Never hard-code hex/rgb or Tailwind palette colours (`text-gray-400`, `bg-slate-800`).
  Never use `dark:` variants; the theme already handles that.
- Spend `primary` sparingly: one primary call to action per view. Use status colours
  (`success` / `warning` / `error`) only for status, never for decoration.
- Prefer `*-soft` variants (`alert-soft`, `badge-soft`, `btn-soft`) for secondary emphasis.
- Sticky or overlaid surfaces need **opaque** backgrounds. A translucent hover or selected
  tint on a sticky cell leaves a visible band. Mix the colour instead:
  `color-mix(in oklab, var(--color-primary) 10%, var(--color-base-100))`.

## 4. daisyUI 5 component rules

- **Inputs with adornments.** Put `input` / `select` on the wrapping `label`, and put
  icons, prefixes, and units inside it:
  ```tsx
  <label className="input input-sm w-full">
    <Icon name="search" className="opacity-60" />
    <input type="search" aria-label="Search name or path" placeholder="Search name or path" />
  </label>
  <label className="select select-sm w-auto">
    <span className="label">Sort</span>
    <select aria-label="Sort">…</select>
  </label>
  ```
- **Dropdowns.** Use `details.dropdown > summary.btn + .dropdown-content` (with
  `menu` for link lists). Do not toggle dropdowns with React state.
- **Grouped controls.** Use `join` / `join-item` for button groups, pagination, and
  input + button pairs. Mark toggle groups with `role="group"` and `aria-pressed`.
- **Selected and active states must be obvious.** On `night`, `btn-active` is almost
  invisible against `base-200`, so don't rely on it. Use these instead:
  - The selected segment of a `join` gets `btn-primary`.
  - An on/off toggle (filter panel open, filter applied) gets
    `border-primary/60 bg-primary/15 text-primary`.
  - Add `filled` to the toggle's `Icon` while it is active.
  - Selection checkboxes use `checkbox-primary`; the default checkmark is too faint.
- **Breadcrumbs.** Use `nav.breadcrumbs > ul > li`. Make the current crumb a `span`
  with `aria-current="page"`, not a link.
- **Size consistency.** Toolbars use one size throughout (`btn-sm`, `input-sm`,
  `select-sm`, `checkbox-sm`). Do not mix sizes in a row.
- **Default variant first.** Reach for `btn` / `btn-ghost` before coloured buttons.
  Use icon-only actions as `btn-ghost btn-square` (or `btn-circle`) with an `aria-label`.
- **Cards and panels.** Use `card` / `card-body` / `card-title` / `card-actions`. Use
  `rounded-box border border-base-content/10 bg-base-200` for a toolbar or panel
  surface.
- **Loading.** Use `Spinner` (`loading loading-spinner`) with a short label. Never use
  plain "Loading…" text alone.
- **Tooltips.** Never rely on the native `title` attribute for action hints; it is
  delayed, inconsistent, and absent on touch. daisyUI `tooltip` is CSS-positioned, so
  it gets clipped inside any `overflow: auto/hidden` ancestor (table viewports, cards,
  scroll panels). Inside such containers, render the tooltip through a portal to
  `document.body` with `position: fixed`, flip it below when there is no room above,
  clamp it to the window edges, and close it on scroll. Show it on hover **and** focus.

## 5. Behaviour and UX rules

- **Hide dead controls.** Remove controls that can never apply (bulk actions with
  nothing selected, play/download on a directory) instead of rendering them disabled.
  Keep a same-width spacer when column alignment matters.
- **Human labels.** Never show internal enum or sort keys in the UI. Map them to
  labels such as `last-check` → "Last check".
- **Placeholders.** Show missing values as "—" or "Unknown", never blank, `null`, or
  `Invalid Date`.
- **Destructive actions.** Use a `Modal` that summarises the impact (counts, sizes,
  linked records). Give errors a retry action, and keep any preview error separate
  from the confirm button.
- **Filters.** Show how many filters are active (a `badge` on the trigger, plus the
  active toggle style above) and always offer a "Clear filters" action.
- **Background refreshes must not flicker.** Show loading rows or spinners only on a
  view's first load. Websocket- or poll-triggered refreshes keep the current rows on
  screen and swap data in place; never clear a list or insert a "Loading" row that
  makes the layout bounce.
- **Responsive.** Toolbars wrap with `flex flex-wrap gap-2`. Advanced panels use a
  responsive grid (`grid-cols-1 sm:grid-cols-2 xl:grid-cols-4`). Wide tables scroll
  inside a `min-w-0` viewport, not the page.

## 6. Accessibility

- Every icon-only control needs an `aria-label`. `Icon` is already `aria-hidden`.
- Wire every form control to a visible `label`, or give it an `aria-label` when the
  visible label sits in a daisyUI `span.label`.
- Use `type="search"` for search fields. They expose the `searchbox` role, so tests
  should query `getByRole("searchbox", …)`.
- Use `role="alert"` for errors and `role="status"` for passive messages.
- Touch targets must be at least 44px on coarse pointers
  (`@media (pointer: coarse)`).
- Keep the visible focus ring. Never remove `outline` without adding a replacement.

## 7. Review checklist

Before finishing UI work:

- [ ] Page shell and `PageHeader` match sibling routes.
- [ ] No hard-coded colours, `dark:` variants, or custom CSS that daisyUI already covers.
- [ ] Toolbars use one control size, and grouped controls use `join`.
- [ ] No disabled-forever controls, raw enum text, or blank cells.
- [ ] Sticky and overlay surfaces are opaque.
- [ ] Selected/active toggles are clearly visible on `night` (no bare `btn-active`).
- [ ] Tooltips are not clipped by scroll containers and never use native `title`.
- [ ] Live/background refreshes do not flash loading states or shift the layout.
- [ ] Empty, loading, error, and not-found states are designed.
- [ ] Accessible names exist for icon-only controls. Tests use role queries.
- [ ] `npm run typecheck`, `eslint`, `prettier --check`, and the colocated Vitest suite pass.
- [ ] The page has been checked visually in the browser at desktop width and around 768px.
