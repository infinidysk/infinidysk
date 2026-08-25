#!/usr/bin/env bash
# Summarize conventional-commit release notes into user-friendly Discord
# announcement text via the OpenAI chat completions API.
#
# Input:  raw notes on stdin (one conventional-commit subject per line).
# Args:   --type stable|rc|dev   release channel, for prompt context
#         --version <tag>        version/tag, for prompt context
# Env:    OPENAI_API_KEY (required; absence exits 1 with an Actions notice)
#         OPENAI_MODEL   (optional; default gpt-5-mini)
#
# Output: summarized notes on stdout. Every failure exits non-zero with empty
# stdout so callers can fall back to posting the raw commit list.
set -euo pipefail

usage() {
  echo "usage: $0 --type stable|rc|dev --version <tag> < raw-notes" >&2
}

fail() {
  echo "::notice::$1 Falling back to raw release notes." >&2
  exit 1
}

TYPE=""
VERSION=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --type) TYPE="${2:-}"; shift 2 ;;
    --version) VERSION="${2:-}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "unknown argument: $1" >&2; usage; exit 2 ;;
  esac
done

case "$TYPE" in
  stable|rc|dev) ;;
  *) echo "--type must be stable, rc, or dev" >&2; usage; exit 2 ;;
esac
[[ -n "$VERSION" ]] || { echo "--version is required" >&2; usage; exit 2; }

if [[ -z "${OPENAI_API_KEY:-}" ]]; then
  echo "::notice::OPENAI_API_KEY is not set; skipping LLM release-notes summary." >&2
  exit 1
fi

MODEL="${OPENAI_MODEL:-gpt-5-mini}"
MAX_INPUT_CHARS=8000
MAX_OUTPUT_CHARS=1500

NOTES=$(cat)
NOTES="${NOTES:0:${MAX_INPUT_CHARS}}"
if [[ -z "${NOTES//[[:space:]]/}" ]]; then
  fail "No release notes provided on stdin."
fi

case "$TYPE" in
  stable) CHANNEL_DESC="a stable release announced to all users" ;;
  rc) CHANNEL_DESC="a release candidate announced to testers" ;;
  dev) CHANNEL_DESC="a rolling development snapshot announced to testers" ;;
esac

SYSTEM_PROMPT=$(cat <<'EOF'
You write Discord release announcements for InfiniDysk, a WebDAV server that mounts NZB documents as a virtual filesystem and streams content directly from Usenet, acting as a drop-in SABnzbd download client for Sonarr, Radarr, and similar tools. Your readers run the Docker image and have no knowledge of the codebase.

You are given conventional-commit messages (feat/fix/chore with scopes). Rewrite them as release notes for end users.

Rules:
- Group changes into short bold-labeled sections: **New**, **Improved**, **Fixed**, and **Before you upgrade** (only when breaking changes or required upgrade actions are present).
- Describe what the user will notice or be able to do, never what code changed. Drop type(scope): prefixes, commit hashes, PR numbers, and issue references.
- Omit chore/ci/docs/test commits and dependency bumps with no user-visible effect.
- Use only Discord-supported markdown: **bold**, *italic*, __underline__, ~~strikethrough~~, - bullets, > quotes, inline `code`, masked links [label](url). Never use tables, images, HTML tags, task lists, # headings, or horizontal rules.
- No @mentions.
- Keep the whole output under 1200 characters.
- Output only the release notes: no preamble, no sign-off, no code fences.
EOF
)

USER_PROMPT=$(printf 'This is %s (version %s).\n\nCommits:\n%s' "$CHANNEL_DESC" "$VERSION" "$NOTES")

PAYLOAD=$(jq -n \
  --arg model "$MODEL" \
  --arg system "$SYSTEM_PROMPT" \
  --arg user "$USER_PROMPT" \
  '{model: $model, messages: [{role: "system", content: $system}, {role: "user", content: $user}], max_completion_tokens: 2000}')

if ! RESPONSE=$(curl --fail-with-body -sS --max-time 60 \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer ${OPENAI_API_KEY}" \
  -d "$PAYLOAD" \
  "https://api.openai.com/v1/chat/completions"); then
  fail "OpenAI summarization request failed."
fi

if ! SUMMARY=$(jq -r '.choices[0].message.content // empty' <<< "$RESPONSE"); then
  fail "Could not parse the OpenAI response."
fi

# Defensive cleanup in case the model wraps the notes in a code fence.
SUMMARY=$(printf '%s\n' "$SUMMARY" | sed -e '1{/^```/d;}' -e '${/^```$/d;}')
SUMMARY="${SUMMARY#"${SUMMARY%%[![:space:]]*}"}"
SUMMARY="${SUMMARY%"${SUMMARY##*[![:space:]]}"}"

[[ -n "$SUMMARY" ]] || fail "OpenAI returned an empty summary."
(( ${#SUMMARY} <= MAX_OUTPUT_CHARS )) || fail "Summary exceeded ${MAX_OUTPUT_CHARS} characters (${#SUMMARY})."
if grep -qE '</?[a-zA-Z][^>]*>' <<< "$SUMMARY"; then
  fail "Summary contained HTML tags, which Discord does not render."
fi
if grep -qE '^[[:space:]]*\|.*\|' <<< "$SUMMARY"; then
  fail "Summary contained a markdown table, which Discord does not render."
fi

printf '%s\n' "$SUMMARY"
