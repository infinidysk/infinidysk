#!/usr/bin/env bash

set -euo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

mkdir "$TMP/bin"
cat > "$TMP/bin/curl" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail

printf '%s\n' "$*" >> "$DISCORD_TEST_ARGUMENTS"

while [[ $# -gt 0 ]]; do
  if [[ "$1" == "-d" ]]; then
    printf '%s\n' "$2" >> "$DISCORD_TEST_OUTPUT"
    exit 0
  fi
  shift
done
EOF
chmod +x "$TMP/bin/curl"

MESSAGE=$(perl -CS -e '
  print "**";
  print "x" x 2500;
  print "\n```\n";
  print "y" x 2500;
  print "\n```\n";
  print "z" x 100;
  print "**\n";
  for my $delimiter ("*", "_", "__", "~~", "||") {
    print $delimiter;
    print "m" x 2500;
    print "$delimiter\n";
  }
  print "\x{1F4A5} " x 1000;
  print "[release notes](https://example.com/release-notes)";
')

post_message() {
  local message=$1
  local output=$2

  printf '%s' "$message" | \
    PATH="$TMP/bin:$PATH" \
    DISCORD_TEST_ARGUMENTS="$TMP/curl-arguments" \
    DISCORD_TEST_OUTPUT="$output" \
    DISCORD_ANNOUNCEMENTS_WEBHOOK_URL="https://example.invalid/webhook" \
    bash "$ROOT/.github/scripts/post-discord-messages.sh"
}

post_message "$MESSAGE" "$TMP/payloads"

perl -CS -MEncode -MJSON::PP -e '
  my $prefix = "_(continued)_\n\n";
  open my $payloads, "<:raw", $ARGV[0] or die "Unable to read payloads: $!\n";
  my $count = 0;
  while (my $json = <$payloads>) {
    next unless length $json;
    my $content = decode_json($json)->{content};
    my $units = 0;
    $units += ord($_) > 0xffff ? 2 : 1 for split //, $content;
    die "Message exceeds Discord UTF-16 limit: $units\n" if $units > 1900;
    die "Continuation prefix missing\n" if $count && index($content, $prefix) != 0;
    $content =~ s/^\Q$prefix\E//;
    for my $delimiter ("**", "__", "*", "_", "~~", "||", "```") {
      die "Markdown delimiter $delimiter was not closed\n" if (() = $content =~ /\Q$delimiter\E/g) % 2;
    }
    $count++;
  }
  die "Expected multiple messages\n" if $count < 2;
  seek $payloads, 0, 0;
  my $all_payloads = do { local $/; <$payloads> };
  die "Markdown link was split\n" unless $all_payloads =~ /\[release notes\]\(https:\/\/example\.com\/release-notes\)/;
' "$TMP/payloads"

PLAIN_MESSAGE=$(perl -e 'print "plain-marker-$_ " for 1..1000;')
post_message "$PLAIN_MESSAGE" "$TMP/plain-payloads"

grep -F -- "--connect-timeout 10" "$TMP/curl-arguments" >/dev/null
grep -F -- "--max-time 30" "$TMP/curl-arguments" >/dev/null

printf '%s' "$PLAIN_MESSAGE" > "$TMP/plain-message"
perl -MJSON::PP -e '
  my $prefix = "_(continued)_\n\n";
  open my $expected_file, "<:raw", $ARGV[0] or die "Unable to read expected message: $!\n";
  my $expected = do { local $/; <$expected_file> };
  open my $payloads, "<:raw", $ARGV[1] or die "Unable to read payloads: $!\n";
  my $actual = "";
  while (my $json = <$payloads>) {
    next unless length $json;
    my $content = decode_json($json)->{content};
    $content =~ s/^\Q$prefix\E//;
    $actual .= $content;
  }
  die "Plain content was dropped or reordered\n" unless $actual eq $expected;
' "$TMP/plain-message" "$TMP/plain-payloads"