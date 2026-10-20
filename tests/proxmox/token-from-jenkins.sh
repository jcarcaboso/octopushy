#!/usr/bin/env bash
# Copies a Proxmox API token from a Jenkins string credential into a local file, without
# printing it. Needs a Jenkins user with Overall/Administer, since only the script console
# can read a credential's value.
#
#   JENKINS_AUTH_FILE   file holding "user:api-token" (mode 0600)
#   JENKINS_URL         the controller, e.g. https://jenkins.example.com
#   CREDENTIAL_ID       default homelab-proxmox-token-umbrel
#   OUTPUT              default .dev/secrets/umbrel-proxmox-token
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
auth_file=${JENKINS_AUTH_FILE:?set JENKINS_AUTH_FILE to a file holding user:api-token}
url=${JENKINS_URL:?set JENKINS_URL}
credential=${CREDENTIAL_ID:-homelab-proxmox-token-umbrel}
output=${OUTPUT:-$root/.dev/secrets/umbrel-proxmox-token}

[[ $credential =~ ^[A-Za-z0-9._-]+$ ]] || { echo "Unexpected credential id: $credential" >&2; exit 1; }
[[ $(stat -c %a "$auth_file") =~ ^[46]00$ ]] || { echo "$auth_file must be readable by you only (chmod 600)." >&2; exit 1; }

# The script console prints the value; nothing else is returned.
script="import com.cloudbees.plugins.credentials.CredentialsProvider
import org.jenkinsci.plugins.plaincredentials.StringCredentials
import hudson.security.ACL
import jenkins.model.Jenkins
def all
try { all = CredentialsProvider.lookupCredentialsInItemGroup(StringCredentials, Jenkins.get(), ACL.SYSTEM2, []) }
catch (MissingMethodException e) { all = CredentialsProvider.lookupCredentials(StringCredentials, Jenkins.get(), ACL.SYSTEM, []) }
def found = all.find { it.id == '$credential' }
print(found == null ? 'MISSING' : 'TOKEN:' + found.secret.plainText)"

umask 077
mkdir -p "$(dirname "$output")"
response=$(mktemp)
trap 'rm -f "$response"' EXIT
# The credentials go to curl on standard input, never on its command line.
auth=$(<"$auth_file")
printf 'user = "%s"\n' "${auth//\"/\\\"}" | curl -sS --fail-with-body -K - \
  --data-urlencode "script=$script" -o "$response" "${url%/}/scriptText" || {
  echo "Jenkins refused the request (is the user an administrator?)." >&2
  exit 1
}

value=$(<"$response")
case $value in
  TOKEN:*) value=${value#TOKEN:} ;;
  MISSING) echo "Jenkins has no string credential '$credential'." >&2; exit 1 ;;
  *) echo "Unexpected answer from the script console (not shown)." >&2; exit 1 ;;
esac
if [[ ! $value =~ ^[^@![:space:]]+@[^![:space:]]+![^=[:space:]]+=[^[:space:]]+$ ]]; then
  echo "The credential does not look like a Proxmox API token (USER@REALM!ID=SECRET)." >&2
  exit 1
fi
printf '%s\n' "$value" >"$output"
echo "Saved the token from '$credential' to $output (token id ${value%%=*})."
