# Local Agent Guidance Design

## Goal

Keep `AGENTS.md` as local operational guidance while preventing it and future local edits from being committed to the InfiniDysk repository. Permit an agent to merge a pull request only when the user explicitly requests that merge.

## Repository changes

- Add `/AGENTS.md` to the repository `.gitignore` so the root guidance file is ignored.
- Remove `AGENTS.md` from the Git index without deleting the working-tree copy.
- Do not rewrite existing Git history; earlier revisions will continue to contain the file.

## Local-only change

Replace the local blanket prohibition on agent PR merges with a rule allowing a merge only after an explicit user request. The ignored local file will not be part of the commit or pull request.

## Verification

- `test -f AGENTS.md` confirms the local file remains present.
- `git check-ignore -v AGENTS.md` confirms the root ignore rule applies.
- `git ls-files --error-unmatch AGENTS.md` must fail, confirming the file is no longer tracked in the resulting tree.
- Inspect the staged and committed diff to confirm it contains only the intended ignore rule, tracked-file deletion, design, and implementation-plan artifacts.

## Delivery

Use the `chore/agents-local-guidance` branch and a `chore(agents)` Conventional Commit. Push the branch and open a pull request to `main`; do not merge that policy-change PR as part of this task.
