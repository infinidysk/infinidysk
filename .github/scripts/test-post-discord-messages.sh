#!/usr/bin/env bash

set -euo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

mkdir "$TMP/bin"
cat > "$TMP/bin/curl" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail

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
  print "\x{1F4A5} " x 1000;
  print "[release notes](https://example.com/release-notes)";
')

printf '%s' "$MESSAGE" | \
  PATH="$TMP/bin:$PATH" \
  DISCORD_TEST_OUTPUT="$TMP/payloads" \
  DISCORD_ANNOUNCEMENTS_WEBHOOK_URL="https://example.invalid/webhook" \
  bash "$ROOT/.github/scripts/post-discord-messages.sh"

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
    die "Bold state was not closed\n" if (() = $content =~ /\*\*/g) % 2;
    die "Code fence state was not closed\n" if (() = $content =~ /```/g) % 2;
    $count++;
  }
  die "Expected multiple messages\n" if $count < 2;
  seek $payloads, 0, 0;
  my $all_payloads = do { local $/; <$payloads> };
  die "Markdown link was split\n" unless $all_payloads =~ /\[release notes\]\(https:\/\/example\.com\/release-notes\)/;
' "$TMP/payloads"