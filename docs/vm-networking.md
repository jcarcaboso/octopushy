# VM networking

The example `proxmox-vm` recipe supports plain DHCP, DHCP with an externally
configured MAC reservation, and a manually configured static IPv4 address.
`network.mode` remains either `dhcp` or `static`; there is no third reservation
mode. Other recipes define their own networking validation and provisioning.

## Plain DHCP

```yaml
network:
  bridge: vmbr0
  mode: dhcp
```

An optional `mac` pins the VM's MAC address without asserting a fixed IP.
OpenTofu passes `dhcp` to Proxmox cloud-init. Verification waits for the QEMU
guest agent to report at least one non-loopback, non-unspecified IPv4 address
and returns the discovered addresses in `ipv4Addresses`; it does not require a
preselected address.

## DHCP with a MAC reservation

Create the reservation on the appropriate DHCP server **before deploying**,
using the same MAC and IPv4 address declared here:

```yaml
network:
  bridge: vmbr0
  mode: dhcp
  mac: "02:00:00:00:00:31"
  reservedAddress: 10.1.0.31
```

`reservedAddress` is an expected address, not a request to configure a router or
DHCP server. Octopushy does not create, update, or confirm the existence of DHCP
reservations or leases. The VM still uses DHCP: no static address or gateway is
sent to cloud-init for this mode. The existing OpenTofu network device uses the
explicit MAC.

Octopushy carries `reservedAddress` into the run's Proxmox context and the
agent's `verify-guest` input as `expectIpv4`. Verification succeeds only when
that address is among the IPv4 addresses reported by the QEMU guest agent, in
addition to the existing presence, placement, name, and running-state checks.
A different address alone does not pass: the existing per-guest verification
retry window ends with a failed address check containing the observed and
expected addresses. No fleet-wide wait or cross-VM barrier is added.

This checks the observed address, not how the guest obtained it or which
interface owns it. If a recipe explicitly disables Proxmox verification with
`verify: none`, no address check runs.

## Manually configured static IPv4

```yaml
network:
  bridge: vmbr0
  mode: static
  address: 10.1.0.31/24
  gateway: 10.1.0.1
  # mac: "02:00:00:00:00:31"  # optional
```

OpenTofu configures the address/prefix and gateway through Proxmox cloud-init.
Verification expects `10.1.0.31`, without the prefix. This does not create a DHCP
reservation.

## Validation and boundaries

The recipe's input schema validates `guest.network` during repository loading:

- Both modes require `bridge` and `mode`.
- `static` requires `address` (IPv4/prefix) and `gateway` (bare IPv4).
- `dhcp` rejects `address` and `gateway`, even when null. Remove those keys
  rather than leaving ignored static settings in a DHCP definition.
- `reservedAddress` is allowed only with `mode: dhcp` and an explicit `mac`.
  It is a bare IPv4 address, without a prefix.
- IPv4 values use four decimal octets from 0–255, without leading zeros;
  static prefixes range from 0–32.
- Any supplied MAC must have six colon-separated hexadecimal octets, be
  unicast, and not be all zeros. Uppercase and lowercase hexadecimal are
  accepted; locally administered unicast MACs are suitable for reservations.
- Existing `vlan` and `dns` settings remain available.

Validation is not an IPAM service: it does not prove subnet/gateway reachability,
detect IP or MAC collisions across VMs, allocate addresses, or check DHCP-server
configuration. Ensure static addresses cannot collide with dynamic leases.
Guests need working QEMU guest-agent reporting for address discovery and
verification. These guarantees apply to this Proxmox recipe and verification
path, not to all possible future providers or custom recipes.
