---
name: infinidysk-review-triage
description: How to address CodeRabbit reviews and GitHub Code Quality / code scanning findings on an infinidysk PR, including replying, resolving threads and checking review state. Use when asked to address review comments, bot findings or PR feedback.
---

# Addressing reviews and scanning findings

## CodeRabbit

1. Reply to every comment with what you fixed (with the commit) or why you skipped or declined it.
2. Resolve **every** CodeRabbit thread, including skipped or declined ones, so CodeRabbit can review again.
3. Check that no CodeRabbit thread is still open, e.g. the `reviewThreads { isResolved }` GraphQL query and `resolveReviewThread`.
4. Check the PR review state. Resolving threads does not dismiss a changes-requested review. If it still blocks the PR, request another review or report the blocker for an authorized maintainer. Never dismiss a review just to bypass protection.

## Code Quality and code scanning

In the same pass:

1. Wait for the current commit's quality and security analyses to finish.
2. List open alerts: `gh api --paginate "repos/<owner>/<repo>/code-scanning/alerts?ref=refs/pull/<n>/merge&state=open"`, and read the PR's review threads from the GitHub bots. Use `--jq` to keep only the fields you need.
3. Fix each finding, or dismiss it with a stated reason when it is a false positive or will not be fixed. Resolve its PR thread, when present.
4. After pushing fixes, wait for that commit's analyses and repeat. If analysis cannot finish, report the pass as incomplete.
