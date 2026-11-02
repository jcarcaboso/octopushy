#!/usr/bin/env bash
# Runs in the container as root, over SSH from lxc-app's provisioners, and runs a recipe's script.
# Everything it needs was uploaded to /run/octopushy-install:
#
#   app/            the recipe's files; $OCTOPUSHY_SCRIPT is one of them
#   environment     NAME=value lines, which this reads and never evaluates
#   secrets.sealed  every secret in one seal, if there are any
#   run.sh          this
#
# OpenTofu cannot tell who it is talking to on SSH, so nothing readable travels that way: the
# seal is made with a key only this container has, and is opened here into
# $OCTOPUSHY_SECRETS/<name>, one file for each secret at mode 0600. Nothing under /run/octopushy-install
# survives this script however it ends, secrets included.
set -euo pipefail

work=/run/octopushy-install
app=$work/app
secrets=$work/secrets

fail() {
  echo "octopushy-install: $*" >&2
  exit 1
}

cleanup() {
  local status=$?
  trap - EXIT
  rm -rf "$work"
  exit "$status"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

# Takes the environment file as it is: a name that is not a plain name, or one of those below that
# change how this shell loads code, is refused, and a value is assigned, never evaluated. This is a
# short list, not a sandbox: the names come from a recipe's reviewed code.
load_environment() {
  local line name value
  while IFS= read -r line || [[ -n $line ]]; do
    [[ -n $line ]] || continue
    [[ $line == *=* ]] || fail "the environment file has a line that is not NAME=value"
    name=${line%%=*}
    value=${line#*=}
    [[ $name =~ ^[A-Z][A-Z0-9_]*$ ]] || fail "the environment file has a name that is not allowed"
    case $name in
      OCTOPUSHY_SCRIPT | OCTOPUSHY_ADDRESS | OCTOPUSHY_NONCES) ;;
      OCTOPUSHY_* | LD_* | BASH* | PATH | IFS | ENV | SHELLOPTS | PS[0-9] | PROMPT_COMMAND | CDPATH | GLOBIGNORE | HOME | SHELL | TMPDIR)
        fail "the environment file sets $name, which a recipe may not" ;;
    esac
    export "$name=$value"
  done <"$work/environment"
}

# Checks what lxc-app made, and only then takes back out what it added, into one file for each
# secret: unseal SEALED KEY IDENTITY SPENT. Nothing is written if it does not check out, and a
# nonce is only ever opened once, which SPENT remembers across installs.
unseal() {
  local alphabet='ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/'
  local nonce cipher tag rest encrypt authenticate block='' plain='' prefix sum i name encoded more
  seal_hash() { printf '%s' "$1" | sha256sum | cut -d' ' -f1; }
  IFS=. read -r nonce cipher tag rest <"$1" || true
  [[ -n $nonce && -n $cipher && -n $tag && -z $rest ]] || fail "the seal is malformed, so it was not opened"
  encrypt=$(seal_hash "octopushy-seal-encrypt:$2")
  authenticate=$(seal_hash "octopushy-seal-authenticate:$2")
  [[ $tag == "$(seal_hash "$authenticate:$(seal_hash "$authenticate:$3:$nonce:$cipher")")" ]] \
    || fail "the seal does not verify: it was changed on its way, or was made for another guest"
  if grep -qxF -e "$nonce" "$4" 2>/dev/null; then
    fail "this seal's nonce was used before, so it was not opened"
  fi
  (umask 077 && mkdir -p "$(dirname "$4")" && printf '%s\n' "$nonce" >>"$4")
  for ((i = 0; i < ${#cipher}; i++)); do
    ((i % 40)) || block=$(printf '%s' "$encrypt:$nonce:$((i / 40))" | sha256sum | cut -d' ' -f1 | tr a-f A-F | basenc --base16 -d | base64 -w0)
    prefix=${alphabet%%"${cipher:i:1}"*}
    sum=${#prefix}
    prefix=${alphabet%%"${block:i%40:1}"*}
    plain+=${alphabet:(sum - ${#prefix} + 64) % 64:1}
  done
  while ((${#plain} % 4)); do plain+='='; done
  # name:base64(value) lines, with no JSON to parse.
  plain=$(printf '%s' "$plain" | base64 -d)
  install -d -m 0700 "$secrets"
  while IFS=: read -r name encoded more || [[ -n $name ]]; do
    [[ $name =~ ^[a-z][a-z0-9_]*$ && -z $more ]] || fail "the seal holds an entry that is not name:value"
    (umask 077 && printf '%s' "$encoded" | base64 -d >"$secrets/$name")
  done <<<"$plain"
}

load_environment
[[ -n ${OCTOPUSHY_SCRIPT:-} && -n ${OCTOPUSHY_ADDRESS:-} && -n ${OCTOPUSHY_NONCES:-} ]] \
  || fail "the environment file lacks what lxc-app always sets"
install -d -m 0700 "$secrets"
if [[ -s $work/secrets.sealed ]]; then
  bootstrap=$(sed -n 's/.* octopushy-bootstrap=\([0-9a-f]\{64\}\)$/\1/p' /root/.ssh/authorized_keys | head -n 1)
  [[ -n $bootstrap ]] || fail "the bootstrap key is missing from authorized_keys; Proxmox writes it only when it creates the container"
  unseal "$work/secrets.sealed" "$bootstrap" "$(</proc/sys/kernel/hostname)/$OCTOPUSHY_ADDRESS" "$OCTOPUSHY_NONCES"
  rm -f "$work/secrets.sealed"
fi

export OCTOPUSHY_WORK=$work OCTOPUSHY_APP=$app OCTOPUSHY_SECRETS=$secrets
cd "$app"
bash "$app/$OCTOPUSHY_SCRIPT"
