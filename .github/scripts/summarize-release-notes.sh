#!/usr/bin/env bash
# Rewrite conventional-commit / release-please notes into Discord-friendly
# user-facing copy via OpenAI. Prints the summary to stdout on success.
# On any failure (missing key, API error, empty/invalid output) exits 1 with
# no stdout so callers can fall back to the raw notes.
#
# Usage:
#   summarize-release-notes.sh --type stable|rc|dev --version <tag>
#
# Required env:
#   OPENAI_API_KEY
#
# Optional env:
#   OPENAI_MODEL          — default: gpt-5.6-luna
#   OPENAI_API_BASE       — default: https://api.openai.com/v1

set -euo pipefail

TYPE=""
VERSION=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --type)
      TYPE="${2:-}"
      shift 2
      ;;
    --version)
      VERSION="${2:-}"
      shift 2
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 1
      ;;
  esac
done

if [[ "$TYPE" != "stable" && "$TYPE" != "rc" && "$TYPE" != "dev" ]]; then
  echo "Usage: $0 --type stable|rc|dev --version <tag>" >&2
  exit 1
fi
if [[ -z "$VERSION" ]]; then
  echo "--version is required" >&2
  exit 1
fi

if [[ -z "${OPENAI_API_KEY:-}" ]]; then
  echo "::notice::OPENAI_API_KEY is not set; skipping LLM summary" >&2
  exit 1
fi

fail() {
  echo "$1" >&2
  exit 1
}

NOTES=$(cat)
if [[ -z "${NOTES// }" ]]; then
  fail "No notes on stdin"
fi

MAX_INPUT=8000
if [[ ${#NOTES} -gt $MAX_INPUT ]]; then
  NOTES="${NOTES:0:$MAX_INPUT}"
  NOTES+=$'\n… (truncated)'
fi

MODEL="${OPENAI_MODEL:-gpt-5.6-luna}"
API_BASE="${OPENAI_API_BASE:-https://api.openai.com/v1}"

SYSTEM_PROMPT=$(cat <<'EOF'
You write Discord announcement copy for InfiniDysk, a WebDAV server that mounts NZB documents as a virtual filesystem and streams from Usenet. It exposes a SABnzbd-compatible API so Sonarr, Radarr, and similar tools can use it as a download client. Readers run the Docker image and care about what they will notice when they pull this build.

Rewrite the source notes (release-please changelog or conventional-commit first lines) into user-facing release notes.

Rules:
- Group related items under these section labels, omitting empty ones: **New**, **Improved**, **Fixed**, and **Before you upgrade** (only if there is a breaking change or an action the user must take).
- Describe what the user will experience. Drop conventional-commit type(scope): prefixes and PR/issue numbers.
- Omit chore, ci, docs, test, and other non-user-visible work, including invisible dependency bumps.
- Discord markdown only: **bold**, *italic*, __underline__, ~~strikethrough~~, - bullets, > quotes, inline `code`, and masked links [label](url). Use **bold** section labels, not # headings.
- Never emit tables, images, HTML, task-list checkboxes, or horizontal rules.
- Do not @mention anyone or write @everyone/@here.
- Keep the entire output under 1200 characters.
- Output only the notes. No preamble, no closing sign-off, no wrapping code fences.
EOF
)

REQUEST=$(jq -n \
  --arg model "$MODEL" \
  --arg system "$SYSTEM_PROMPT" \
  --arg type "$TYPE" \
  --arg version "$VERSION" \
  --arg notes "$NOTES" \
  '{
    model: $model,
    max_completion_tokens: 800,
    messages: [
      {role: "system", content: $system},
      {role: "user", content: ("Kind: \($type)\nVersion: \($version)\n\nSource notes:\n\($notes)")}
    ]
  }')

TMP=$(mktemp)
trap 'rm -f "$TMP"' EXIT

if ! curl --silent --show-error --fail-with-body --max-time 60 \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer ${OPENAI_API_KEY}" \
  -d "$REQUEST" \
  -o "$TMP" \
  "${API_BASE}/chat/completions"; then
  fail "OpenAI request failed: $(head -c 500 "$TMP" 2>/dev/null || true)"
fi

SUMMARY=$(jq -r '.choices[0].message.content // empty' "$TMP")
if [[ -z "${SUMMARY}" || "$SUMMARY" == "null" ]]; then
  fail "OpenAI response had no summary content"
fi

if printf '%s\n' "$SUMMARY" | awk 'NR==1 && /^```/ {found=1} END {exit !found}'; then
  SUMMARY=$(printf '%s\n' "$SUMMARY" | sed '1d' | sed '$ {/^```$/d;}')
fi

SUMMARY="${SUMMARY#"${SUMMARY%%[![:space:]]*}"}"
SUMMARY="${SUMMARY%"${SUMMARY##*[![:space:]]}"}"

if [[ -z "$SUMMARY" ]]; then
  fail "Summary was empty after trimming"
fi

if [[ ${#SUMMARY} -gt 1500 ]]; then
  fail "Summary exceeded 1500 characters (${#SUMMARY})"
fi

if printf '%s\n' "$SUMMARY" | grep -qE '^\s*\|.*\|'; then
  fail "Summary contains a markdown table"
fi

if printf '%s\n' "$SUMMARY" | grep -qE '!\[[^]]*\]\('; then
  fail "Summary contains a markdown image"
fi

if printf '%s\n' "$SUMMARY" | grep -qiE '<(html|div|span|p|br/?|img|table|thead|tbody|tr|td|th|ul|ol|li|h[1-6])[ >]'; then
  fail "Summary contains HTML"
fi

if printf '%s\n' "$SUMMARY" | grep -qE '@(everyone|here)([^[:alnum:]_]|$)|<@!?[0-9]+>|<@&[0-9]+>'; then
  fail "Summary contains a Discord mention"
fi

printf '%s\n' "$SUMMARY"
