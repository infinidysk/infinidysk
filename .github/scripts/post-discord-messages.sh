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
  binmode STDOUT, ":raw";
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

  sub new_markdown_state {
    return { bold => 0, fenced => 0 };
  }

  sub advance_markdown_state {
    my ($state, $token) = @_;
    if ($token eq "```") {
      $state->{fenced} = !$state->{fenced};
    } elsif (!$state->{fenced} && $token eq "**") {
      $state->{bold} = !$state->{bold};
    }
  }

  sub markdown_state_for {
    my ($text) = @_;
    my $state = new_markdown_state();
    while ($text =~ /```|\[[^\]\r\n]*\]\([^\)\r\n]*\)|`[^`\r\n]*`|\\\X|\*\*|\X/g) {
      advance_markdown_state($state, $&);
    }
    return $state;
  }

  sub closing_markdown {
    my ($state) = @_;
    my $closing = "";
    $closing .= "\n```" if $state->{fenced};
    $closing .= "**" if $state->{bold};
    return $closing;
  }

  sub opening_markdown {
    my ($state) = @_;
    my $opening = "";
    $opening .= "**" if $state->{bold};
    $opening .= "```\n" if $state->{fenced};
    return $opening;
  }

  my $message = "";
  my $state = new_markdown_state();
  my $last_safe_break = 0;
  TOKEN: while ($value =~ /```|\[[^\]\r\n]*\]\([^\)\r\n]*\)|`[^`\r\n]*`|\\\X|\*\*|\X/g) {
    my $token = $&;
    my $token_units = utf16_units($token);

    while (1) {
      my $available = $max - ($message_number ? utf16_units($prefix) : 0);
      # Reserve enough room to close and reopen combined bold and fenced-code state.
      my $content_budget = $available - 13;
      if (utf16_units($message) + $token_units <= $content_budget) {
        $message .= $token;
        advance_markdown_state($state, $token);
        if (!$state->{fenced} && !$state->{bold} && $token =~ /\s\z/) {
          $last_safe_break = length($message);
        }
        next TOKEN;
      }

      if ($last_safe_break) {
        my $tail = substr($message, $last_safe_break);
        emit(substr($message, 0, $last_safe_break));
        $message = $tail;
        $state = markdown_state_for($message);
        $last_safe_break = 0;
        next;
      }

      my $closing = closing_markdown($state);
      if (length($message)) {
        emit($message . $closing);
        $message = opening_markdown($state);
        next;
      }

      die "A single Markdown construct exceeds the Discord UTF-16 content limit\n";
    }
  }

  emit($message . closing_markdown($state));
' > "$TMP"

while IFS= read -r -d '' body; do
  escaped_body=$(printf '%s' "$body" | jq -Rsa .)
  curl --fail-with-body -sS -H "Content-Type: application/json" \
    -d "{\"content\": ${escaped_body}, \"flags\": 4, \"allowed_mentions\": {\"parse\": []}}" \
    "$DISCORD_ANNOUNCEMENTS_WEBHOOK_URL" >/dev/null
done < "$TMP"