---
name: infinidysk-commits
description: infinidysk Conventional Commit types, scopes, breaking-change rules and user-facing PR titles, with examples. Use before writing a commit message, creating a PR, or editing a PR title.
---

# Commits and PR titles

Format: `<type>(<scope>): <short imperative description>`. release-please reads it.

| Type | Use for |
|------|---------|
| `feat` | New user-visible behavior |
| `fix` | Bug fixes, regressions, compatibility fixes |
| `perf` | Performance-only improvements |
| `refactor` | Internal restructuring worth a release note (rare; prefer `chore`) |
| `build` | Packaging operators care about (otherwise `chore(docker)` / `chore(build)`) |
| `revert` | Reverts |
| `chore` | **All** non-behavior work: CI, tests only, docs, `AGENTS.md`, skills, tooling, repo hygiene |

- No bare `docs` / `ci` / `test` / `tests` types; use `chore(<scope>)`. A change that does not alter application behavior is `chore` on both commit and PR title, so release notes leave it out.
- **Breaking changes** (user-visible breaks, irreversible/destructive migrations) use `!` and/or a `BREAKING CHANGE:` footer. Routine additive migrations are plain `feat(db)` / `fix(db)` with a "back up config before upgrading" note in the PR.
- `chore(main): release …` is reserved for release-please. Never edit `CHANGELOG.md` by hand.
- Small, focused commits; one logical change each. No vague messages (`wip`, `fix bug`), no unrelated changes in one commit.

Scopes (smallest accurate one; omit only for small cross-cutting changes): `nntp`, `yenc`, `stream`, `archive`, `par2`, `queue`, `sab`, `webdav`, `mount`, `db`, `import`, `api`, `auth`, `health`, `ui`, `deps`, `ci`, `docs`, `test`, `agents`, `skills`, `docker`.

```
feat(queue): verify and repair multipart sets with PAR2
fix(webdav): return 416 for range requests past the end of a file
perf(stream): prefetch the next segments before the reader asks
feat(db)!: rewrite the item schema
chore(ci): cache the rapidyenc build
```

## PR titles

Same `<type>(<scope>): <description>` format; never omit the type. Describe the change for **end users**: what is fixed or introduced from their point of view, not how. Commit messages carry the technical detail.

| Commit message (technical) | PR title (user-facing) |
|----------------------------|------------------------|
| `fix(nntp): count a dropped provider connection as one failure` | `fix(nntp): one dropped connection no longer shows up as several provider errors` |
| `fix(api): count only failures the caller sees as errors` | `fix(ui): overview errors only count failures you would notice` |
| `fix(webdav): clamp infinite-depth PROPFIND to depth 1` | `fix(webdav): prevent mounts hanging on large directory listings` |

For changes with no user-visible effect (`chore`, internal refactors) a concise technical title is fine.
