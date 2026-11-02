#!/bin/sh
# Installs the locked Ansible environment. The Ansible image and the agent image both use
# this, so they hold the same thing. Run from a directory holding the files of agent/ansible.
set -eu
prefix=/opt/octopushy/ansible
python3 -m venv "$prefix"
"$prefix/bin/pip" install --no-cache-dir --require-hashes --no-deps -r requirements.lock
install -D -m 0755 octopushy-ansible "$prefix/lib/octopushy/octopushy-ansible"
install -D -m 0644 octopushy_recap.py "$prefix/lib/octopushy/octopushy_recap.py"
sed -i "1c #!$prefix/bin/python3" "$prefix/lib/octopushy/octopushy-ansible"
ln -s "$prefix/lib/octopushy/octopushy-ansible" /usr/local/bin/octopushy-ansible
