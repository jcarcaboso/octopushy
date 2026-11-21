# Waking a Proxmox host

A host whose `installation.yaml` gives it `wakeOnLan` can be woken from its installation's page while it is not
known to be online: offline, not observed yet, or its installation's last observation failed. A standalone host that is
down fails its observation, which keeps the last one's "online", so a failed observation is enough. With one host,
**Wake &lt;host&gt;** sits next to **Observe now**; with several, each such host's row in **Hosts** has its own.

```yaml
hosts:
  moraband:
    datastores: { images: local, disks: local-lvm }
    wakeOnLan:
      mac: "a8:a1:59:3c:0e:72"
      broadcast: 10.1.0.255   # optional; default 255.255.255.255
```

Turn Wake-on-LAN on in the host's firmware and on its network interface (`ethtool -s <nic> wol g`) first.

## Who sends the packet

A magic packet is a broadcast, so it wakes the host only from its own network segment, and no agent can tell
beforehand whether it is on it. So every connected agent that announces `wake-on-lan-v1` sends one, from its own
process, to UDP port 9 of `broadcast`. Nothing comes back: the host's next observation says whether it woke. A
packet is harmless to send twice, so asking again is fine.

An agent in a container on a bridge network, such as the internal agent of `deploy/compose.yaml`, sends into that
network and never reaches the LAN. One that runs on the host network, or on another machine of the host's segment,
does. An agent cannot wake the host it runs on.

## What it refuses

The installation must be valid, managed, and trusted: an observed-only installation (`manage: false`) wakes nothing,
and its hosts show no button. The server sends only the MAC and address from the installation's definition, and
answers 409 when the host has no `wakeOnLan` or no connected agent can send it.
