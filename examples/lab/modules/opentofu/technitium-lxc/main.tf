locals {
  address    = split("/", var.guest.network.address)[0]
  technitium = var.guest.inputs.technitium
  firewall   = var.guest.inputs.firewall
  work       = "/run/octopushy-technitium"

  # OpenTofu can check SSH's host key only against a public key, and nothing can give it this
  # container's before it connects, so whoever answers on the network gets what is uploaded.
  # The password is therefore sealed to this key, which reaches the container only as the
  # comment of an SSH key in Proxmox's API, and never over SSH. It is derived from the private
  # key in this stack's state, so it needs no provider of its own.
  bootstrap     = sha256("octopushy-bootstrap:${tls_private_key.install.private_key_openssh}")
  bootstrap_key = "${trimspace(tls_private_key.install.public_key_openssh)} octopushy-bootstrap=${local.bootstrap}"
  seal_alphabet = split("", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/")

  dns_sources   = length(local.firewall.dnsSources) == 0 ? null : join(",", local.firewall.dnsSources)
  admin_sources = length(local.firewall.adminSources) == 0 ? null : join(",", local.firewall.adminSources)
}

# Lets OpenTofu reach the container, whose template has no way in otherwise. Proxmox has no
# user-data or exec for LXC. The private key lives in this guest's state.
resource "tls_private_key" "install" {
  algorithm = "ED25519"
}

module "container" {
  source = "../proxmox-lxc"

  octopushy      = var.octopushy
  host           = var.host
  template       = var.template
  extra_ssh_keys = [local.bootstrap_key]

  guest = merge(var.guest, {
    inputs = {
      startup = var.guest.inputs.startup
      firewall = {
        enabled = local.firewall.enabled
        # Everything else is dropped, including Technitium's plain HTTP port 5380.
        rules = [
          { proto = "udp", dport = "53", source = local.dns_sources, comment = "DNS" },
          { proto = "tcp", dport = "53", source = local.dns_sources, comment = "DNS" },
          { proto = "tcp", dport = "53443", source = local.admin_sources, comment = "Technitium API and cluster traffic" },
          { proto = "tcp", dport = "22", source = local.admin_sources, comment = "SSH, to install from OpenTofu" },
        ]
      }
    }
  })
}

# A provisioner only runs when its resource is created, so what should install again replaces
# this resource. A failed install taints it, so the next apply tries again.
resource "terraform_data" "install" {
  depends_on = [module.container]

  triggers_replace = {
    container         = module.container.incarnation
    version           = local.technitium.version
    sha256            = local.technitium.sha256
    aspnetcoreVersion = local.technitium.aspnetcoreVersion
    aspnetcoreSha512  = local.technitium.aspnetcoreSha512
    script            = filesha256("${path.module}/install.sh")
  }

  connection {
    type        = "ssh"
    host        = local.address
    user        = "root"
    private_key = tls_private_key.install.private_key_openssh
    timeout     = "5m"
  }

  provisioner "remote-exec" {
    inline = ["install -d -m 0700 ${local.work}"]
  }

  provisioner "file" {
    source      = "${path.module}/install.sh"
    destination = "${local.work}/install.sh"
  }

  # OpenTofu has no way to encrypt, so this adds, mod 64, the password's base64 to a keystream
  # of SHA-256 blocks under the key above and a nonce that differs from plan to plan. install.sh
  # takes it back out. Each block gives 40 of its 43 full characters.
  provisioner "file" {
    content = join("", [for i, c in split("", replace(base64encode(var.admin_password), "=", "")) :
      local.seal_alphabet[(index(local.seal_alphabet, c) + index(local.seal_alphabet,
    substr(base64sha256("${local.bootstrap}:${plantimestamp()}:${floor(i / 40)}"), i % 40, 1))) % 64]])
    destination = "${local.work}/admin-password.sealed"
  }

  provisioner "remote-exec" {
    inline = [join(" ", [
      "TECHNITIUM_VERSION=${local.technitium.version}",
      "TECHNITIUM_SHA256=${local.technitium.sha256}",
      "ASPNETCORE_VERSION=${local.technitium.aspnetcoreVersion}",
      "ASPNETCORE_SHA512=${local.technitium.aspnetcoreSha512}",
      "GUEST_ADDRESS=${local.address}",
      "SEAL_NONCE=${plantimestamp()}",
      "NAMESERVERS='${join(" ", var.guest.network.dns)}'",
      "bash ${local.work}/install.sh",
    ])]
  }
}
