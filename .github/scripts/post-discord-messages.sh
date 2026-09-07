#!/usr/bin/env bash
# Post stdin to Discord in ordered messages below Discord's content limit.

set -euo pipefail

: "${DISCORD_ANNOUNCEMENTS_WEBHOOK_URL:?DISCORD_ANNOUNCEMENTS_WEBHOOK_URL is not set}"
if [[ ! "$DISCORD_ANNOUNCEMENTS_WEBHOOK_URL" =~ ^https://[^[:space:]]+$ ]]; then
  echo "DISCORD_ANNOUNCEMENTS_WEBHOOK_URL must use HTTPS" >&2
  exit 1
fi

TMP=$(mktemp)
RESPONSE=$(mktemp)
trap 'rm -f "$TMP" "$RESPONSE"' EXIT

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
    return { fenced => 0, spans => {}, stack => [] };
  }

  sub copy_markdown_state {
    my ($state) = @_;
    return {
      fenced => $state->{fenced},
      spans => { %{ $state->{spans} } },
      stack => [ @{ $state->{stack} } ],
    };
  }

  sub advance_markdown_state {
    my ($state, $token) = @_;
    if ($token eq "```") {
      $state->{fenced} = !$state->{fenced};
    } elsif (!$state->{fenced} && $token =~ /^(?:\*\*|__|~~|\|\|)$/) {
      if ($state->{spans}{$token}) {
        $state->{spans}{$token} = 0;
        @{ $state->{stack} } = grep { $_ ne $token } @{ $state->{stack} };
      } else {
        $state->{spans}{$token} = 1;
        push @{ $state->{stack} }, $token;
      }
    }
  }

  sub markdown_state_for {
    my ($text) = @_;
    my $state = new_markdown_state();
    while ($text =~ /```|\[[^\]\r\n]*\]\([^\)\r\n]*\)|`[^`\r\n]*`|\\\X|\*\*|__|~~|\|\||\X/g) {
      advance_markdown_state($state, $&);
    }
    return $state;
  }

  sub closing_markdown {
    my ($state) = @_;
    my $closing = "";
    $closing .= "\n```" if $state->{fenced};
    $closing .= $_ for reverse @{ $state->{stack} };
    return $closing;
  }

  sub opening_markdown {
    my ($state) = @_;
    my $opening = "";
    $opening .= $_ for @{ $state->{stack} };
    $opening .= "```\n" if $state->{fenced};
    return $opening;
  }

  my $message = "";
  my $state = new_markdown_state();
  my $last_safe_break = 0;
  TOKEN: while ($value =~ /```|\[[^\]\r\n]*\]\([^\)\r\n]*\)|`[^`\r\n]*`|\\\X|\*\*|__|~~|\|\||\X/g) {
    my $token = $&;
    my $token_units = utf16_units($token);

    while (1) {
      my $available = $max - ($message_number ? utf16_units($prefix) : 0);
      my $candidate_state = copy_markdown_state($state);
      advance_markdown_state($candidate_state, $token);
      if (utf16_units($message . $token . closing_markdown($candidate_state)) <= $available) {
        $message .= $token;
        $state = $candidate_state;
        if (!$state->{fenced} && !@{ $state->{stack} } && $token =~ /\s\z/) {
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
  while true; do
    if ! status=$(curl -sS --connect-timeout 10 --max-time 30 \
      --output "$RESPONSE" --write-out '%{http_code}' -H "Content-Type: application/json" \
      -d "{\"content\": ${escaped_body}, \"flags\": 4, \"allowed_mentions\": {\"parse\": []}}" \
      "$DISCORD_ANNOUNCEMENTS_WEBHOOK_URL"); then
      exit 1
    fi

    if [[ "$status" =~ ^2[0-9]{2}$ ]]; then
      break
    fi

    if [[ "$status" != "429" ]]; then
      cat "$RESPONSE" >&2
      exit 1
    fi

    if ! retry_after=$(jq -er '.retry_after | numbers | select(. >= 0 and . <= 3600)' "$RESPONSE"); then
      echo "Discord rate-limit response did not include a valid retry_after delay" >&2
      exit 1
    fi
    sleep "$retry_after"
  done
done < "$TMP"