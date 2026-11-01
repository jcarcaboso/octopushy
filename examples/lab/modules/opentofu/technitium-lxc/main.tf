locals {
  address    = split("/", var.guest.network.address)[0]
  technitium = var.guest.inputs.technitium
  firewall   = var.guest.inputs.firewall
  work       = "/run/octopushy-technitium"

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
  extra_ssh_keys = [tls_private_key.install.public_key_openssh]

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

  # /run is a tmpfs, and install.sh removes the file however it ends.
  provisioner "file" {
    content     = var.admin_password
    destination = "${local.work}/admin-password"
  }

  provisioner "remote-exec" {
    inline = [join(" ", [
      "TECHNITIUM_VERSION=${local.technitium.version}",
      "TECHNITIUM_SHA256=${local.technitium.sha256}",
      "ASPNETCORE_VERSION=${local.technitium.aspnetcoreVersion}",
      "ASPNETCORE_SHA512=${local.technitium.aspnetcoreSha512}",
      "GUEST_ADDRESS=${local.address}",
      "NAMESERVERS='${join(" ", var.guest.network.dns)}'",
      "bash ${local.work}/install.sh",
    ])]
  }
}
