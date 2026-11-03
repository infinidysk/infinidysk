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

payload=""
response=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    -d) payload=$2; shift 2; continue ;;
    --output) response=$2; shift 2; continue ;;
  esac
  shift
done

printf '%s\n' "$payload" >> "$DISCORD_TEST_ATTEMPTS"
if [[ "${DISCORD_TEST_RATE_LIMIT_ONCE:-false}" == "true" && ! -e "$DISCORD_TEST_RATE_LIMIT_USED" ]]; then
  touch "$DISCORD_TEST_RATE_LIMIT_USED"
  printf '%s' '{"retry_after":0}' > "$response"
  printf '429'
  exit 0
fi

: > "$response"
printf '%s\n' "$payload" >> "$DISCORD_TEST_OUTPUT"
printf '204'
EOF
chmod +x "$TMP/bin/curl"

cat > "$TMP/bin/sleep" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$1" >> "$DISCORD_TEST_SLEEP"
EOF
chmod +x "$TMP/bin/sleep"

MESSAGE=$(perl -CS -e '
  print "**";
  print "x" x 2500;
  print "\n```\n";
  print "y" x 2500;
  print "\n```\n";
  print "z" x 100;
  print "**\n";
  for my $delimiter ("__", "~~", "||") {
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
  local rate_limit_once=${3:-false}

  printf '%s' "$message" | \
    PATH="$TMP/bin:$PATH" \
    DISCORD_TEST_ARGUMENTS="$TMP/curl-arguments" \
    DISCORD_TEST_ATTEMPTS="$TMP/curl-attempts" \
    DISCORD_TEST_OUTPUT="$output" \
    DISCORD_TEST_RATE_LIMIT_ONCE="$rate_limit_once" \
    DISCORD_TEST_RATE_LIMIT_USED="$TMP/rate-limit-used" \
    DISCORD_TEST_SLEEP="$TMP/sleep-delays" \
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
    for my $delimiter ("**", "__", "~~", "||", "```") {
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

BULLET_MESSAGE=$(perl -e 'print "* bullet-marker-$_\n" for 1..1000;')
post_message "$BULLET_MESSAGE" "$TMP/bullet-payloads"

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

printf '%s' "$BULLET_MESSAGE" > "$TMP/bullet-message"
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
  die "Markdown bullets were altered\n" unless $actual eq $expected;
' "$TMP/bullet-message" "$TMP/bullet-payloads"

RATE_LIMIT_MESSAGE="rate-limit-marker $(printf 'r%.0s' {1..2500})"
rm -f "$TMP/rate-limit-used"
: > "$TMP/curl-attempts"
post_message "$RATE_LIMIT_MESSAGE" "$TMP/rate-limit-payloads" true

perl -MJSON::PP -e '
  open my $attempts, "<:raw", $ARGV[0] or die "Unable to read attempts: $!\n";
  my @attempts = <$attempts>;
  die "Expected a retried webhook request\n" if @attempts < 2;
  die "Rate-limited request was not retried before the next chunk\n"
    unless $attempts[0] eq $attempts[1];
' "$TMP/curl-attempts"
grep -Fx '0' "$TMP/sleep-delays" >/dev/null