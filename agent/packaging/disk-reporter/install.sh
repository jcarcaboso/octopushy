#!/bin/sh
# Run on the guest after provisioning. DESTDIR stages files without touching systemd.
set -eu

if [ "$#" -ne 3 ]; then
    echo "usage: install.sh BINARY CONFIG.json TOKEN_FILE" >&2
    exit 2
fi
root=${DESTDIR:-}
if [ -z "$root" ] && [ "$(id -u)" != 0 ]; then
    echo "Run as root on the guest, or set DESTDIR to stage an installation." >&2
    exit 1
fi
here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
interval=$("$1" interval "$2")
token=$(cat "$3")
case "$token" in
    *[!0-9a-fA-F]*|'') echo "Invalid reporting token." >&2; exit 1 ;;
esac
if [ "${#token}" -ne 64 ]; then
    echo "Invalid reporting token." >&2
    exit 1
fi
unset token

config="$root/etc/octopushy-disk-reporter"
units="$root/etc/systemd/system"
install -d -m 0755 "$root/usr/local/bin" "$config" "$units"
install -m 0755 "$1" "$root/usr/local/bin/octopushy-disk-reporter"
install -m 0644 "$2" "$config/config.json"
install -m 0600 "$3" "$config/token"
install -m 0644 "$here/octopushy-disk-reporter.service" "$units/"
install -m 0644 "$here/octopushy-disk-reporter.timer" "$units/"
install -d -m 0755 "$units/octopushy-disk-reporter.timer.d"
jitter=$((interval / 10))
if [ "$jitter" -gt 60 ]; then jitter=60; fi
cat > "$units/octopushy-disk-reporter.timer.d/interval.conf" <<EOF
[Timer]
OnUnitActiveSec=
OnBootSec=2min
OnUnitActiveSec=${interval}s
RandomizedDelaySec=${jitter}s
EOF
chmod 0644 "$units/octopushy-disk-reporter.timer.d/interval.conf"

if [ -z "$root" ]; then
    systemctl daemon-reload
    systemctl enable octopushy-disk-reporter.timer
    systemctl restart octopushy-disk-reporter.timer
    echo "Installed. Reporting every ${interval}s; inspect journalctl -u octopushy-disk-reporter.service."
else
    echo "Staged installation in $root (timer not enabled)."
fi
