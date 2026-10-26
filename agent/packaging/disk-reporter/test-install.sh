#!/bin/sh
# No root, systemd daemon, or network required.
set -eu
here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
binary=${1:-"$here/../../target/debug/octopushy-disk-reporter"}
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT HUP INT TERM
printf '%s\n' '{"endpoint":"https://octopushy.test/api/guests/test/disk-usage","intervalSeconds":1800}' > "$work/config.json"
printf '%064d\n' 0 > "$work/token"
DESTDIR="$work/root" sh "$here/install.sh" "$binary" "$work/config.json" "$work/token"
test "$(stat -c %a "$work/root/etc/octopushy-disk-reporter/token")" = 600
test "$(stat -c %a "$work/root/etc/octopushy-disk-reporter/config.json")" = 644
test "$(stat -c %a "$work/root/usr/local/bin/octopushy-disk-reporter")" = 755
test "$("$binary" interval "$work/root/etc/octopushy-disk-reporter/config.json")" = 1800
expected=$(printf '[Timer]\nOnUnitActiveSec=\nOnBootSec=2min\nOnUnitActiveSec=1800s\nRandomizedDelaySec=60s\n')
test "$(cat "$work/root/etc/systemd/system/octopushy-disk-reporter.timer.d/interval.conf")" = "$expected"
test ! -e "$work/root/etc/systemd/system/timers.target.wants"
printf '%s\n' '{"endpoint":"https://octopushy.test/api/guests/test/disk-usage","intervalSeconds":60}' > "$work/config.json"
DESTDIR="$work/root" sh "$here/install.sh" "$binary" "$work/config.json" "$work/token"
expected=$(printf '[Timer]\nOnUnitActiveSec=\nOnBootSec=2min\nOnUnitActiveSec=60s\nRandomizedDelaySec=6s\n')
test "$(cat "$work/root/etc/systemd/system/octopushy-disk-reporter.timer.d/interval.conf")" = "$expected"
printf '%s\n' '{"endpoint":"http://insecure.test"}' > "$work/config.json"
if DESTDIR="$work/bad" sh "$here/install.sh" "$binary" "$work/config.json" "$work/token"; then
    echo "Installer accepted an insecure endpoint." >&2
    exit 1
fi
test ! -e "$work/bad"
echo "Installer checks passed."
