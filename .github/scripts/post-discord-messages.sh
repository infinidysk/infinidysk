#!/usr/bin/env bash
# Post stdin to Discord in ordered messages below Discord's content limit.

set -euo pipefail

: "${DISCORD_ANNOUNCEMENTS_WEBHOOK_URL:?DISCORD_ANNOUNCEMENTS_WEBHOOK_URL is not set}"
if [[ ! "$DISCORD_ANNOUNCEMENTS_WEBHOOK_URL" =~ ^https://[^[:space:]]+$ ]]; then
  echo "DISCORD_ANNOUNCEMENTS_WEBHOOK_URL must use HTTPS" >&2
  exit 1
fi

TMP=$(mktemp)
trap 'rm -f "$TMP"' EXIT

perl -CS -MEncode -e '
  binmode STDIN, ":raw";
  my $value = Encode::decode("UTF-8", do { local $/; <STDIN> });
  my $prefix = "_(continued)_\n\n";
  my $max = 1900;
  my $message_number = 0;

  sub utf16_units {
    my ($text) = @_;
    my $units = 0;
    $units += ord($_) > 0xffff ? 2 : 1 for split //, $text;
    return $units;
  }

  sub emit {
    my ($text) = @_;
    return unless length($text);
    print Encode::encode("UTF-8", ($message_number ? $prefix : "") . $text), "\0";
    $message_number++;
  }

  sub split_line {
    my ($line, $available) = @_;
    my @chunks;
    my $chunk = "";
    my $units = 0;
    for my $grapheme ($line =~ /\X/g) {
      my $grapheme_units = utf16_units($grapheme);
      if ($units + $grapheme_units > $available) {
        # Prefer whitespace boundaries so Markdown constructs remain intact.
        if ($chunk =~ /^(.*\s)(.*)$/s) {
          push @chunks, $1;
          $chunk = $2;
          $units = utf16_units($chunk);
        } else {
          push @chunks, $chunk;
          $chunk = "";
          $units = 0;
        }
      }
      $chunk .= $grapheme;
      $units += $grapheme_units;
    }
    push @chunks, $chunk if length($chunk);
    return @chunks;
  }

  my $message = "";
  for my $line ($value =~ /.*(?:\n|\z)/g) {
    next unless length($line);
    my $available = $max - ($message_number ? length(Encode::decode("UTF-8", $prefix)) : 0);
    if (utf16_units($message . $line) <= $available) {
      $message .= $line;
      next;
    }
    emit($message);
    $message = "";
    $available = $max - length(Encode::decode("UTF-8", $prefix));
    for my $chunk (split_line($line, $available)) {
      if (utf16_units($chunk) > $available) {
        die "Unable to split a Discord message within the UTF-16 content limit\n";
      }
      emit($chunk);
      $available = $max - length(Encode::decode("UTF-8", $prefix));
    }
  }

  emit($message);
' > "$TMP"

while IFS= read -r -d '' body; do
  escaped_body=$(printf '%s' "$body" | jq -Rsa .)
  curl --fail-with-body -sS -H "Content-Type: application/json" \
    -d "{\"content\": ${escaped_body}, \"flags\": 4, \"allowed_mentions\": {\"parse\": []}}" \
    "$DISCORD_ANNOUNCEMENTS_WEBHOOK_URL" >/dev/null
done < "$TMP"