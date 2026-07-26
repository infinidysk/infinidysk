# Redesign Proposal: Replace React Router 7 with htmx + Web Components

Status: proposal for brainstorm synthesis, not a decision. Written against the codebase as of the
`feature/provider-usage-stats` merge (commit `7505209`). All file:line citations point at
`frontend/` and `backend/` as they exist today; see `docs/arc42/_research/frontend.md` and
`docs/arc42/adr/ADR-007-frontend-ssr-and-proxy.md` for the prior analysis this builds on.

## 0. Headline recommendation, up front

**Eliminate the Node/frontend process entirely and have the .NET backend render the htmx UI
directly (option (b) in the brief).** This is not primarily a resource-footprint argument (though
it helps) — it's that option (b) *deletes* three weak points the existing research already flagged,
rather than adding a fourth runtime to route around them:

1. The six-prefix proxy-route list hand-duplicated in `server.ts` (compression filter) and
   `server/app.ts` (routing), "no test guards this" (`frontend.md` §4) — gone, because there's only
   one process and one route table.
2. The `FRONTEND_BACKEND_API_KEY` inter-process trust boundary (CLAUDE.md's own framing: "the trust
   boundary between them") and the duplicated key-attachment logic between the proxy's
   `setApiKeyForAuthenticatedRequests` and every method in `backend-client.server.ts` — gone,
   because there's no inter-process call to authenticate.
3. `entrypoint.sh`'s `wait_either`/dual-PID supervision dance (ADR-008, flagged as the
   highest-priority deployment-view gap: "a crash in either process takes down the entire
   container... no independent restart of just the crashed half") — gone, because there's one
   process to supervise, and Docker's own restart policy + a trivial `HEALTHCHECK` become
   sufficient without s6-overlay/supervisord ever entering the picture.

The concrete container-level win, verified against the actual `Dockerfile` (root, stage 3):
`FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine` then `RUN ... apk add --no-cache nodejs npm
...`. Node is not "already there for other reasons" — it is installed *specifically* to run this
frontend. Dropping option (b) removes: the entire `frontend-build` Docker stage (Node.js image,
`npm install`, `npm run build`, `npm run build:server`, `npm prune`), the `apk add nodejs npm`
runtime line, `frontend/node_modules` (246 top-level packages, 210MB, 447 total packages per
`package-lock.json` — **verified counts**, not hypothesis), and the copy of `dist-node/server.js` +
`build/` into the final image. What's left in the container is the existing backend base image
plus static assets. This is the single biggest footprint/complexity lever in this entire redesign
effort, exactly as the brief anticipated — and it's achievable because `backend/Program.cs:87,129`
already shows a plain `AddControllers()`/`MapControllers()` ASP.NET Core host, which Razor Pages
drops into with two lines (`AddRazorPages()`, `MapRazorPages()`), no architectural fight required.

The single biggest risk is **not** htmx accumulating jQuery-style spaghetti in the abstract — it's
this specific app's three genuinely stateful UI surfaces (live socket, uploader, settings
dirty-tracking) growing ad-hoc, untested hand-written JavaScript with no framework discipline to
lean on, in a codebase that already has zero frontend tests today. §8 proposes a hard cap.

**Correction to two premises in the assignment brief, both surfaced by this codebase's own
evidence** (flagging these now so the rest of this document isn't built on them):

- *"htmx's `ws` extension... can consume the existing backend `/ws` topic-based protocol directly,
  potentially with zero backend changes"* — not accurate here. htmx's `ws` extension swaps **HTML
  fragments** received over the socket into the DOM. This app's wire format is deliberately
  non-HTML and compact: `'qp'` (queue percentage) is `"qs-nzo_id"` unwrapped to a single number
  string, `'cxs'` (connection stats) is `"0|0|0|0|1|0"`, `'qa'`/`'ha'` are raw JSON (see
  `queue/controllers/websocket-controller.ts:5-21`, `settings/usenet/usenet.tsx:177-187`). Backend
  changes ARE required either way (§3) — the `ws` extension doesn't apply to this protocol as-is.
- *"the frontend and backend are two independently-run applications"* framing in CLAUDE.md is true
  today but is precisely the constraint option (b) removes — worth stating since it's the premise
  most of the rest of the codebase's docs are written against.

---

## 1. Where does the server-rendering live?

### Option (a): keep a thin Node/Express process

Strip React Router's SSR pipeline (`app/routes/*`, `@react-router/fs-routes`, Vite's SSR build)
down to a template renderer (e.g. `express` + `ejs`/`nunjucks`/plain template literals) that still
does the same three jobs `server.ts`/`server/app.ts` do today: proxy the six path prefixes, gate
session auth, relay the websocket. Smallest diff from today's shape; `entrypoint.sh` is unchanged;
`FRONTEND_BACKEND_API_KEY` boundary is unchanged. Real cost: you still ship Node.js + npm +
`node_modules` in the final image, you still hand-maintain the six-prefix route list in (now fewer,
but still two) places, and you still have two processes to supervise per ADR-008. This buys almost
nothing over today's architecture except deleting React/Vite/SSR — worth doing only as a stopgap if
(b) is judged too large a single change.

### Option (b): the .NET backend renders the HTML fragments — recommended

Concretely, this means:

- `builder.Services.AddRazorPages()` (or plain MVC `AddControllersWithViews()` if View-style
  layouts are preferred) alongside the existing `AddControllers()` at `Program.cs:87`.
  `app.MapRazorPages()` next to `app.MapControllers()` at `Program.cs:129`. Razor Pages colocate a
  `.cshtml` template with a small code-behind (`OnGet`/`OnPost`), which maps naturally onto today's
  route-folder-with-loader-and-action shape (`app/routes/queue/route.tsx`'s `loader`/`action`
  become a Razor PageModel's `OnGet`/`OnPostAsync`).
- htmx fragment responses are just Razor **partial views** (`_QueueRows.cshtml` etc.) returned from
  either a Razor PageModel handler or, just as easily, an ordinary `[ApiController]` action that
  returns `PartialViewResult` — the existing `Api/Controllers/*` family can grow htmx-facing
  actions right next to the JSON-facing ones without a parallel framework.
- Static assets (the built CSS, any vendored JS files for Web Components, Bootstrap's own JS
  bundle) are served via `app.UseStaticFiles()` from `wwwroot/`, standard ASP.NET Core, no build
  step required for htmx/Web Components themselves (see §5).
- Session auth becomes `AddAuthentication().AddCookie(...)` — ASP.NET Core's built-in cookie auth
  scheme, functionally replacing `authentication.server.ts`'s `createCookieSessionStorage`. This is
  the single largest *specific* line item this migration adds that isn't already "delete code": the
  login/session/redirect-to-login/websocket-upgrade-auth logic currently in
  `auth-middleware.server.ts` + `authentication.server.ts` (\~signed cookie, `PUBLIC_PATHS`
  allowlist, dual `Request`/`IncomingMessage` auth check for HTTP vs WS upgrade) has a direct but
  non-trivial ASP.NET Core equivalent, and **existing `__session` cookies do not carry over** — this
  is a forced one-time re-login for every user on cutover, not a silent migration. Budget for it
  explicitly; it is real work, not a footnote.
- The `/ws` endpoint (`websocketManager.HandleRoute`, `Program.cs:128`) currently authenticates
  Node's single outbound relay connection by having it send `FRONTEND_BACKEND_API_KEY` as its first
  message (`websocket.server.ts:78`). Under option (b) the **browser connects to `/ws` directly** —
  it must never hold that shared secret. This requires the backend to add a second, cookie-based
  auth path to the WS upgrade handshake (the ASP.NET Core cookie auth middleware already validates
  the request's cookie before `HandleRoute` runs, so this is "gate the upgrade behind
  `[Authorize]`/`HttpContext.User.Identity.IsAuthenticated`", not a new protocol). The per-topic
  `lastMessage` replay cache currently living in the Node process (`websocket.server.ts:9,31`,
  in-memory `Map<string,string>`, "resets on restart") needs an equivalent
  `ConcurrentDictionary<string,string>` inside the backend's existing `websocketManager` — small,
  but a real code addition, not zero backend changes.
- The `downloadKey` capability token (`downloads.server.ts`'s `getDownloadKey`/`verifyDownloadKey`,
  `sha256(path + FRONTEND_BACKEND_API_KEY)`) currently computes on one side (Node, at directory-list
  render time) and verifies on the other (backend, at stream time) across the process boundary via
  the shared key. Collapsing to one process doesn't just remove a network hop — it removes the
  *reason* the two halves of this scheme needed to agree on a shared secret at all; the token logic
  can move to a single backend-side helper used by both the Razor page (to embed the link) and the
  streaming handler (to verify it), with no key transport required. (Whether the backend's existing
  verification has an expiry is a currently-open question per `frontend.md` §"OPEN QUESTIONS" —
  orthogonal to this migration, but worth fixing at the same time since the code is being touched
  anyway.)

**Effort framing**: option (b) is *more* backend work than option (a) but *categorically less
total* work than "keep two runtimes and also rewrite the UI" — because option (a) still requires
rewriting all ~15 route trees to a non-React templating approach while gaining none of the
process-elimination benefits. Given the UI rewrite has to happen either way (that's the whole
point of this brainstorm), option (b) is the better place to land the same amount of rewrite effort.

---

## 2. Route-by-route migration plan

| Route family | Replacement shape | Notes |
|---|---|---|
| `_index` (dashboard/nav shell) | Static Razor layout (`_Layout.cshtml`) + `hx-boost` on nav links for SPA-like transitions without full reloads | `top-navigation`/`left-navigation`/`hamburger-menu` are pure chrome, no client state beyond CSS-driven collapse — plain CSS/HTML |
| `login` | Server-rendered form, plain `<form method="post">`, no htmx needed at all | Today's version is a React Router `Form`+`action` already isomorphic to a plain HTML POST (`login/route.tsx:44-54`) — this is a *no-op* rewrite, arguably the easiest route in the app |
| `onboarding` | Same as `login`, plus the four `submitButtonDisabled`/`submitButtonText` states (username/password/confirm-match checks, `onboarding/route.tsx:34-48`) reproduced as ~10 lines of inline vanilla JS on `input`/`change`, no component needed | Pure client-side field validation gating a submit button — htmx doesn't help here, a Web Component would be overkill; plain `<script>` |
| `health` | Razor page for the initial table (`GetHealthCheckQueue`/`GetHealthCheckHistory`), `<nzbdav-live-socket>` custom element (§4) patches rows on `'hs'`/`'hp'` topic events by `data-dav-item-id` selector; the "top up to 15 items" `useEffect` refetch (`health/route.tsx:46-57`) becomes `hx-get` on a `hx-trigger="load"` polling swap, or is simply dropped in favor of always server-rendering enough rows | Easy route — no cross-tab or multi-step state |
| `queue` | Razor page renders the initial queue+history tables; `<nzbdav-live-socket>` (§4) owns the one WS connection and patches/inserts/removes `<tr>` rows keyed by `nzo_id` for `qs`/`qp`/`qa`/`qr`/`ha`/`hr` topics; row action buttons (pause/remove/priority change) become `hx-post` with `hx-target` set to the row; **file upload is the one place needing a genuine Web Component** — `<nzbdav-uploader>` (§4) | The `disableLiveView` bandaid (queue/route.tsx:50, "Live view is disabled... proper pagination will be added soon") carries over unchanged — it's a data-volume problem, not a framework one |
| `settings` (+ `arrs`, `cache`, `library`, `maintenance/*`, `rclone`, `repairs`, `sabnzbd`, `usenet`, `webdav`) | **The hardest route — treat deliberately, not as "just a form".** One Razor page per tab (`hx-get` swaps the tab body into a shared container, `hx-push-url` for deep-linking to `/settings#usenet` equivalence), OR all 9 tabs server-rendered into one page with CSS-only tab switching (`<input type=radio>` + `:checked` sibling selectors) if payload size is acceptable. Either way, the cross-tab "is anything dirty" tracking that today lives in 8 separate `isXUpdated` predicates feeding one `isUpdated` boolean plus a `useBlocker`+confirm-modal (`settings/route.tsx:105-114,237-256`) needs **one** `<nzbdav-settings-form>` custom element wrapping the whole tab set: it listens for `input`/`change` bubbling up from any descendant field, diffs against a serialized "original" snapshot it holds, toggles the save button's enabled state/label, and installs a `beforeunload` handler while dirty. This is exactly the kind of state that outlives a single request and is the right kind of thing to give a Web Component | Usenet tab's provider-add modal (`usenet.tsx:560-852`) has its own local gate (`canSave = isFormValid && (connectionTested \|\| type == Disabled)`, line 666) — this is small enough to be a plain `<dialog>` element with `hx-post` to `/api/test-usenet-connection` swapping a status partial in, no custom element needed beyond the shared form wrapper already covering it |
| `settings.update` | A Razor Page handler / API action taking the same changed-keys diff as today (`settings.update/route.tsx`), returning a swapped "Saved ✅" fragment | Direct port, no design question |
| `explore` | Razor page listing directory entries as plain `<a href="/view/...">` — **unchanged from today**, since these are already plain anchor tags bypassing the SPA framework entirely (`explore/route.tsx:88`, and ADR-007 already found SSR was never in this hot path). Directory navigation (`Link to={getDirectoryPath(...)}`) becomes `hx-get` swapping the listing `<div>`, or even plain links with server-rendered breadcrumbs — no client state at all | The **easiest** route in the app together with `login` — say so plainly, it makes the harder cases credible by contrast. The item-menu "preview" (`item-menu.tsx:29`) links straight to the same `/view` URL; there is no in-app video player/seek UI today, so there's no existing "video-seek Web Component" to migrate — if one is added later, that would be a legitimate future custom-element candidate, but it's out of scope because it doesn't exist yet |

---

## 3. Real-time updates without React state management

**Recommendation: keep the existing `{Topic, Message}` wire protocol byte-for-byte, and consume it
from a single hand-written custom element, not htmx's `ws`/SSE extensions.**

Why not htmx's extensions: both `ws` and `sse` extensions are built around the assumption that the
server pushes **HTML** which gets swapped into a target element. This protocol pushes compact
non-HTML deltas by design — `'qp'` is a bare percentage number, `'cxs'` is six pipe-delimited
integers, `'qa'`/`'ha'` are JSON blobs the client currently reshapes into row objects
(`websocket-controller.ts:30-41`). Converting every tick of `'qp'` (fired per-queue-item, likely the
highest-frequency topic in the app) into a server-rendered `<tr>` fragment would multiply the
bytes-per-update for no behavioral gain, and would require the *backend* to become a stateful HTML
row-renderer for every topic rather than a plain pub/sub relay — a strictly worse position than
today's.

Concrete design: `<nzbdav-live-socket topics="qs,qp,qa,qr,ha,hr">` (attribute-configured per page,
so `queue`, `health`, and `usenet`'s settings tab each declare only the topics they need) opens one
`WebSocket` to same-origin `/ws` on `connectedCallback`, subscribes with the existing
`{topic: mode}` JSON handshake, and on message dispatches a `CustomEvent('nzbdav:topic', {detail:
{topic, message}})` that bubbles up the DOM. Page-level vanilla JS (or small per-page
`<script>` blocks, not more custom elements) listens for that event and does the same imperative DOM
patching React currently does via `setQueueSlots`/`setHistorySlots` — except now it's
`document.querySelector('[data-nzo-id="..."]').textContent = ...` instead of a state setter.
Reconnect-with-backoff (`setTimeout(() => connect(), 1000)`, identical across
`websocket-controller.ts`, `health/route.tsx`, `usenet.tsx`, `remove-unlinked-files.tsx` today —
four copy-pasted implementations already) collapses into this one element's `disconnectedCallback`/
reconnect logic, written once.

This is a straightforward, direct-connection replacement for the *browser-facing* half of today's
relay: since there's no more Node process, the browser talks to the backend's `/ws` directly instead
of through the Node relay — eliminating a full hop, at the cost of the backend now needing to
authenticate browser-originated WS connections by cookie (§1) instead of trusting a single
pre-authenticated relay connection.

---

## 4. Where a Web Component earns its keep

Discipline constraint stated up front (see also §8): **exactly four custom elements, no more,
enumerated here — resist adding a fifth by habit.**

1. **`<nzbdav-live-socket>`** — owns the WebSocket connection and topic dispatch (§3). Justified
   because this state (an open socket, subscription list, reconnect timer) must survive arbitrary
   htmx-driven DOM swaps elsewhere on the page without reconnecting or losing its subscriptions —
   exactly the "outlives a single request/response" test from the brief.
2. **`<nzbdav-uploader>`** — wraps the existing `XMLHttpRequest`-based upload flow
   (`nzb-upload-controller.ts`): drag-and-drop zone, a client-side upload *queue* (multiple files
   dropped at once, uploaded serially — `uploadQueueRef`/`processUploadQueue`,
   `nzb-upload-controller.ts:15-93`), and **per-file upload progress** via `xhr.upload.progress`
   events. This is the second and starkest reason a plain `hx-post
   hx-encoding="multipart/form-data"` doesn't suffice: htmx's form-post swap has no upload-progress
   event hook at all — the only way to keep the existing "drag 5 .nzb files in, watch each one's
   progress bar independently" UX is to keep owning the `XMLHttpRequest` directly, in a component
   that renders its own progress rows and dispatches a completion event the page listens for to
   trigger a fragment refresh of the real queue table.
3. **`<nzbdav-settings-form>`** — wraps the entire settings page's tab set (§2): dirty-tracking
   against a held-in-memory "original config" snapshot, aggregate save-button enable/label state,
   and a `beforeunload` guard replacing today's `useBlocker` + `ConfirmModal`
   (`settings/route.tsx:114,210-218,237-256`). Justified because "is anything on this whole
   multi-tab form dirty" is state that has to be computed across sibling subtrees that don't
   otherwise talk to each other — a natural fit for one element owning the aggregate and letting
   plain field-level `input`/`change` events bubble up to it, rather than either a global-window
   variable (fragile) or re-fetching/diffing on every keystroke via htmx round-trips (wasteful and
   laggy for a save-button label).
4. **`<nzbdav-connection-bar>`** (small, optional — could also just be inline SVG/CSS updated
   directly by the live-socket listener) — the usenet settings tab's live per-provider connection
   gauge (`usenet.tsx:316-328`, `connections[index].live/active/max` driving two overlaid bar
   widths) and the 30-day usage sparkline are the kind of small, self-contained visual widget that's
   *reasonable* as a component but not *required* — if the team wants to keep the component count at
   three, this one can just be a `<div>` whose inline `style.width` the page-level listener sets
   directly, same as the tables. Listed here to be explicit that it was considered and is a judgment
   call, not an oversight.

**Everywhere else is htmx alone, deliberately**: `explore` navigation (`hx-get` swapping a listing
partial, or plain links — no state), `health`'s table (server-rendered + patched by the shared
live-socket listener, no dedicated component), row actions everywhere (`hx-post`/`hx-delete` with
`hx-confirm` for destructive maintenance actions, `hx-target`/`hx-swap` to update just the affected
row or a status line), the usenet provider add/edit dialog (plain `<dialog>` + `hx-post` to the
existing connection-test endpoint), and all of `login`/`onboarding`/`_index` chrome. The point of
this migration is fewer moving parts — recreating React's component model in Web Components by
habit (a component per visual widget) would forfeit that.

---

## 5. Build tooling

htmx is a single vendored `.js` file (no npm package required, though `npm install htmx.org` works
fine if preferred for version pinning). Vanilla Web Components need zero build step — no bundler,
no transpiler, no JSX. Concretely, the `frontend/` `npm run build`/dev-loop today
(`react-router build`, `react-router typegen && tsc -b`, Vite SSR module loading in dev) is replaced
by:

- **Dev loop**: `dotnet watch run` on the backend (already exists as a .NET workflow) picks up
  `.cshtml` changes without a rebuild (Razor Pages are compiled on save in dev by default); static
  JS/CSS are just files under `wwwroot/`, edited and reloaded directly, no watch process needed at
  all beyond the browser's own reload.
- **Build**: `dotnet publish` already produces the full deployable output — there is no separate
  frontend build step because there is no separate frontend.
- **Type generation**: React Router's `typegen` step (`npm run typecheck`) and its whole reason for
  existing (typed `Route.LoaderArgs`/`Route.ComponentProps` per route) goes away entirely — Razor
  Pages get compile-time checking from the C# compiler on the PageModel side, and templates are
  plain HTML with `@Model.Whatever` interpolation, checked by the Razor compiler at build time
  (a real, if different, form of type safety — not "no safety").

**Does the container need Node.js/npm/`node_modules` at all afterward?** No — under option (b), the
final image's `apk add --no-cache nodejs npm` line (root `Dockerfile`, stage 3) is deleted outright,
and the entire `frontend-build` stage (stage 1) is deleted. This is the direct, verifiable
realization of the point raised in §1 and the brief's point 5 — not a hypothesis, since the
Dockerfile's Node dependency is explicit and load-bearing only for running the thing being replaced.

One tooling note found during this research, not previously flagged: **Tailwind is already close to
dead weight in the current app.** `@tailwindcss/vite` is wired into `vite.config.ts:13`, but
`frontend/app/app.css` — the one global stylesheet — contains no `@tailwind`/`@import "tailwindcss"`
directive at all; styling is done almost entirely through CSS Modules (`*.module.css` files
alongside every component) and hand-written Bootstrap overrides. This means the Tailwind
build-pipeline dependency can likely be dropped with near-zero visual impact independent of this
migration — worth confirming with a grep-for-utility-class-usage pass before relying on it, but it's
a strong signal that "no build step" isn't fighting against real, load-bearing Tailwind usage today.

Bootstrap itself (currently `bootstrap` + `react-bootstrap` npm packages) has a direct downgrade
path: `react-bootstrap`'s `Tabs`/`Accordion`/`Modal` components are thin React wrappers around
Bootstrap 5's own vanilla-JS behavior — vendoring `bootstrap.bundle.min.js` (Bootstrap's own build,
no React binding) as a static asset under `wwwroot/` reproduces the same tab/accordion/modal
behavior with `data-bs-toggle="tab"` attributes on plain HTML, zero framework required.

---

## 6. Migration strategy

**Incremental, not big-bang — coexistence is straightforward because the proxy already has the
right shape for it.**

1. Add one new prefix to the *existing* Express proxy list (`server/app.ts`'s path check,
   `frontend.md` §1): e.g. any request under `/ui2/*` (or, more simply, any request the backend
   explicitly owns) is proxied to the backend instead of falling through to React Router SSR.
2. Stand up Razor Pages in the backend for one low-risk route first — `explore` and `login` are the
   best starting candidates per §2 (already effectively static, no cross-tab state, lowest risk if
   something's subtly wrong). They ship as backend-rendered pages reachable at their real URLs
   (`/explore/*`, `/login`) by having the frontend's proxy route those specific paths to the backend
   instead of to React Router, while every other route continues to hit React Router exactly as
   today.
3. Migrate `health`, `queue` (its live-socket + row patching), then `settings` last (the hardest,
   per §2) — each migration is "move one more path prefix from the SSR branch to the backend-proxy
   branch," fully reversible by moving the prefix back if a page regresses.
4. Once every route is backend-rendered, delete `app/routes/*`, `react-router.config.ts`,
   `server/app.ts`'s SSR branch, and finally the frontend process and Dockerfile stage entirely —
   the very last step, not the first.

This mirrors exactly the incremental pattern ADR-007 already recommended for the smaller `ssr:false`
experiment, just carried one step further to a full backend cutover instead of stopping at "SPA
served by the same Node process."

---

## 7. Concrete complexity/footprint argument

Verified, not estimated, where a command could confirm it:

- **`frontend/node_modules`**: 246 top-level packages, 210MB on disk, 447 packages total counting
  transitive dependencies (`package-lock.json`, `.packages` keys) — this entire tree disappears
  under option (b).
- **Docker image**: the `frontend-build` stage (a full `node:alpine` build image, `npm install` +
  two build steps + `npm prune`) and the `apk add --no-cache nodejs npm` line in the final runtime
  stage are both deleted (verified against the actual `Dockerfile`, not inferred).
- **`entrypoint.sh`**: 151 lines today, of which roughly half (`wait_either`, the `BACKEND_PID`/
  `FRONTEND_PID` dance, the backend-health-then-start-frontend polling loop, `terminate()`'s
  dual-kill) exist solely to coordinate two processes. Single-process deployment collapses this to
  "run migration, then run the app," with Docker's own restart policy and a `HEALTHCHECK` doing what
  `wait_either` does today — directly resolving the ADR-008 gap rather than working around it.
- **Type/build tooling removed**: `@react-router/dev`, `@react-router/express`,
  `@react-router/fs-routes`, `@react-router/node`, `vite` + its SSR build mode, `tsx`, the
  `typecheck` script (`react-router typegen && tsc -b`) as a PR gate — replaced by the C# compiler
  already gating backend PRs today.
- **(hypothesis)** A junior contributor reading a `.cshtml` template with `@Model.Something`
  interpolation and an `OnGet`/`OnPostAsync` handler needs to understand less machinery than one
  reading a React Router route file that participates in SSR hydration, typed loader/action
  generics, and client-side re-render semantics — this is a real claim about cognitive load but
  isn't independently measured here.
- **What does NOT shrink**: the actual UI logic — 8 settings tabs' worth of form fields, validation
  rules, and the ~15 route trees' worth of display logic all have to be re-expressed somewhere.
  This migration changes *where* that logic lives and *what host process runs it*, not how much of
  it exists. Don't oversell the win as "less UI code" — it's "fewer runtimes, fewer processes,
  fewer trust boundaries, no build pipeline," which is a real and large win on its own terms.

---

## 8. Effort, risk, and the spaghetti question

**Effort** for a solo/small-team maintainer: this is a full rewrite of every route (~15 route trees
including subroutes) plus new backend work (Razor Pages wiring, cookie auth, WS auth path, the
`lastMessage` cache move) that has no equivalent in option (a). It is *not* small. It is, per §1,
better-spent effort than option (a) because the UI rewrite is unavoidable either way and option (b)
buys the process-elimination win for roughly the same total motion. Expect this to be the largest
single change discussed in the whole redesign brainstorm, matching the brief's own framing.

**Single biggest risk: the three genuinely stateful surfaces (`<nzbdav-live-socket>`,
`<nzbdav-uploader>`, `<nzbdav-settings-form>`) accumulating hand-rolled, untested complexity over
time with no framework guardrails — this codebase's own jQuery-adjacent future.** This app has zero
frontend tests today (stated as a given, not a blocker) and htmx/Web Components don't change that;
if anything, moving state management to imperative DOM manipulation removes React's "state in, DOM
out" discipline that at least made bugs somewhat mechanical to reason about. The concrete mitigation
this document commits to, so a future maintainer (or reviewer) can hold the team to it:

- **Hard cap: four custom elements, named in §4, full stop.** A pull request proposing a fifth
  needs to explain in the PR description which existing element it should have been a mode of
  instead, before it's added.
- **No state sharing between custom elements except via DOM events (bubbling `CustomEvent`) or
  plain HTML attributes** — never a shared JS module-level variable, never one element reaching
  into another's internals. This is the exact discipline that prevented jQuery-era spaghetti in
  well-run jQuery codebases and is equally available (and equally skippable) here.
- **One directory convention for server-rendered fragments** — e.g. `Pages/Shared/_*.cshtml` for
  every htmx-swap partial, so "what does this `hx-get` return" is always answerable by one naming
  pattern, mirroring the concern already raised about the *current* app's two-file proxy-list
  duplication (don't trade one hand-maintained parallel list for another).
- Given zero tests exist today, this migration is a reasonable point to add the *first* tests to
  this codebase, scoped narrowly to the four stateful elements' logic (extractable as plain
  functions taking/returning plain data, testable without a DOM at all) — not a blocker to shipping
  the migration, but the natural moment to start, since these are new files rather than legacy ones
  to retrofit.
