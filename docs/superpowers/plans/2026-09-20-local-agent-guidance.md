# Local Agent Guidance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep root `AGENTS.md` as local-only guidance, permit explicitly requested PR merges, and prevent the file from being tracked in future commits.

**Architecture:** Update the working-tree policy first, then add a root-anchored ignore rule and remove only the index entry with `git rm --cached`. Existing history remains unchanged, while the local file survives and future edits stay ignored.

**Tech Stack:** Markdown, Git ignore rules, Git index operations, GitHub CLI

---

### Task 1: Update the local merge policy

**Files:**
- Modify locally only: `AGENTS.md:410-437`

- [ ] **Step 1: Replace the existing-PR merge restriction**

Change step 7 under “Updating an existing pull request” to:

```markdown
7. Do not merge the pull request unless the user explicitly requests that exact merge.
```

- [ ] **Step 2: Replace the blanket merge prohibition**

Replace the “Never merge pull requests” section with:

```markdown
### Merging pull requests

Agents may merge a pull request only when the user explicitly requests that exact merge.

- Do not infer merge authorization from requests to implement, commit, push, deploy, release, or open a PR.
- Do not enable auto-merge unless the user explicitly requests auto-merge.
- Before merging, verify the requested PR number, repository, head SHA, required checks, and review state.
- After merging, report the merge commit and resulting PR state.
```

- [ ] **Step 3: Verify the local policy text**

Run:

```bash
rg -n "explicitly requests that exact merge|Never merge pull requests" AGENTS.md
```

Expected: two matches for the explicit-request rule and no match for `Never merge pull requests`.

### Task 2: Stop tracking root AGENTS.md

**Files:**
- Modify: `.gitignore:4`
- Remove from index, retain locally: `AGENTS.md`

- [ ] **Step 1: Add the root ignore rule**

Insert this entry beneath the “non-project files” heading in `.gitignore`:

```gitignore
/AGENTS.md
```

- [ ] **Step 2: Remove only the Git index entry**

Run:

```bash
git rm --cached AGENTS.md
```

Expected: `rm 'AGENTS.md'`; the working-tree file remains present.

- [ ] **Step 3: Verify local retention and future ignore behavior**

Run:

```bash
test -f AGENTS.md
git check-ignore -v AGENTS.md
test -z "$(git ls-files AGENTS.md)"
```

Expected: all commands exit zero, and `git check-ignore` identifies the new `/AGENTS.md` rule.

- [ ] **Step 4: Review the pending repository change**

Run:

```bash
git status --short
git diff -- .gitignore
git diff --cached -- AGENTS.md
```

Expected: `.gitignore` is modified, `AGENTS.md` is staged as deleted, and the local policy edits are absent from the repository diff.

- [ ] **Step 5: Commit the tracking policy**

Run:

```bash
git add .gitignore
git commit -m "chore(agents): keep guidance local"
```

Expected: a focused commit containing the ignore rule and deletion of tracked `AGENTS.md`.

### Task 3: Verify and deliver

**Files:**
- Verify: `.gitignore`
- Verify local-only: `AGENTS.md`

- [ ] **Step 1: Verify the committed tree and working tree**

Run:

```bash
test -f AGENTS.md
git check-ignore -v AGENTS.md
test -z "$(git ls-files AGENTS.md)"
git status --short --branch
git log -3 --oneline
```

Expected: the file exists locally, is ignored, is absent from the index, and the branch has no uncommitted changes.

- [ ] **Step 2: Push the branch**

Run:

```bash
git push -u origin chore/agents-local-guidance
```

Expected: the remote branch is created and configured as the upstream.

- [ ] **Step 3: Open the pull request**

Run:

```bash
gh pr create --base main --head chore/agents-local-guidance \
  --title "chore(agents): keep agent guidance local" \
  --body $'## Summary\n- stop tracking the root AGENTS.md file\n- ignore local agent guidance going forward\n- document the local-guidance policy and implementation\n\n## Test plan\n- verify AGENTS.md remains locally present\n- verify Git ignores AGENTS.md\n- verify AGENTS.md is absent from git ls-files'
```

Expected: GitHub returns the URL of a new PR targeting `main`. Do not merge this policy-change PR as part of this implementation task.

- [ ] **Step 4: Verify PR metadata**

Run:

```bash
gh pr view --json number,url,baseRefName,headRefName,state
```

Expected: `state` is `OPEN`, `baseRefName` is `main`, and `headRefName` is `chore/agents-local-guidance`.
