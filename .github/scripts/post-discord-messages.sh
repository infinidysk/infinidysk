#!/usr/bin/env bash
# Post stdin to Discord in ordered messages below Discord's content limit.

set -euo pipefail

: "${DISCORD_ANNOUNCEMENTS_WEBHOOK_URL:?DISCORD_ANNOUNCEMENTS_WEBHOOK_URL is not set}"
if [[ ! "$DISCORD_ANNOUNCEMENTS_WEBHOOK_URL" =~ ^https://[^[:space:]]+$ ]]; then
  echo "DISCORD_ANNOUNCEMENTS_WEBHOOK_URL must use HTTPS" >&2
  exit 1
fi

MAX_LEN=1900
CONTINUATION_PREFIX=$'_(continued)_\n\n'
TMP=$(mktemp)
trap 'rm -f "$TMP"' EXIT

perl -CS -MEncode -e '
  binmode STDIN, ":raw";
  my $value = Encode::decode("UTF-8", do { local $/; <STDIN> });
  my $prefix = "_(continued)_\n\n";
  my $max = 1900;
  my $units = 0;
  my $message = "";
  my $message_number = 0;

  for my $character (split //, $value) {
    my $character_units = ord($character) > 0xffff ? 2 : 1;
    my $available = $max - ($message_number ? length(Encode::decode("UTF-8", $prefix)) : 0);
    if ($units + $character_units > $available) {
      print Encode::encode("UTF-8", ($message_number ? $prefix : "") . $message), "\0";
      $message = "";
      $units = 0;
      $message_number++;
    }
    $message .= $character;
    $units += $character_units;
  }

  print Encode::encode("UTF-8", ($message_number ? $prefix : "") . $message), "\0"
    if length($message);
' > "$TMP"

while IFS= read -r -d '' body; do
  escaped_body=$(printf '%s' "$body" | jq -Rsa .)
  curl --fail-with-body -sS -H "Content-Type: application/json" \
    -d "{\"content\": ${escaped_body}, \"flags\": 4, \"allowed_mentions\": {\"parse\": []}}" \
    "$DISCORD_ANNOUNCEMENTS_WEBHOOK_URL" >/dev/null
done < "$TMP"