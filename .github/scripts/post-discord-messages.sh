#!/usr/bin/env bash
# Post stdin to Discord in ordered messages below Discord's content limit.

set -euo pipefail

: "${DISCORD_ANNOUNCEMENTS_WEBHOOK_URL:?DISCORD_ANNOUNCEMENTS_WEBHOOK_URL is not set}"

MAX_LEN=1900
CONTINUATION_PREFIX=$'_(continued)_\n\n'
CHUNK=""
POSTED=false

post_chunk() {
  local body="$1"
  if [[ -z "$body" ]]; then
    return
  fi

  if [[ "$POSTED" == "true" ]]; then
    body="${CONTINUATION_PREFIX}${body}"
  fi

  local escaped_body
  escaped_body=$(printf '%s' "$body" | jq -Rsa .)
  curl --fail-with-body -sS -H "Content-Type: application/json" \
    -d "{\"content\": ${escaped_body}, \"flags\": 4, \"allowed_mentions\": {\"parse\": []}}" \
    "$DISCORD_ANNOUNCEMENTS_WEBHOOK_URL" >/dev/null
  POSTED=true
}

flush_chunk() {
  post_chunk "$CHUNK"
  CHUNK=""
}

while IFS= read -r line || [[ -n "$line" ]]; do
  available=$MAX_LEN
  if [[ "$POSTED" == "true" ]]; then
    available=$((available - ${#CONTINUATION_PREFIX}))
  fi

  while [[ ${#line} -gt $available ]]; do
    if [[ -n "$CHUNK" ]]; then
      flush_chunk
      available=$((MAX_LEN - ${#CONTINUATION_PREFIX}))
    fi
    post_chunk "${line:0:$available}"
    line="${line:$available}"
    available=$((MAX_LEN - ${#CONTINUATION_PREFIX}))
  done

  candidate=""
  if [[ -n "$CHUNK" ]]; then
    candidate+=$'\n'
  fi
  candidate+="$line"
  if [[ ${#candidate} -gt $available ]]; then
    flush_chunk
    CHUNK="$line"
  else
    CHUNK="$candidate"
  fi
done

flush_chunk