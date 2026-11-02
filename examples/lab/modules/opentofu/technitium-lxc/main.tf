locals {
  technitium = var.guest.inputs.technitium
  firewall   = var.guest.inputs.firewall

  dns_sources   = length(local.firewall.dnsSources) == 0 ? null : join(",", local.firewall.dnsSources)
  admin_sources = length(local.firewall.adminSources) == 0 ? null : join(",", local.firewall.adminSources)
}

# The container, its SSH access, and running files/install.sh in it with the admin password
# sealed to it. lxc-app does all of that; docs/lxc-recipes.md says how. What this root adds is
# the settings of Technitium.
module "app" {
  source = "../lxc-app"

  octopushy = var.octopushy
  host      = var.host
  template  = var.template
  guest     = var.guest

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

  app = {
    files  = "${path.module}/files"
    script = "install.sh"
    # The script reads these as data, so their checks in variables.tf are for correctness, not
    # for what reaches a shell.
    environment = {
      TECHNITIUM_VERSION = local.technitium.version
      TECHNITIUM_SHA256  = local.technitium.sha256
      ASPNETCORE_VERSION = local.technitium.aspnetcoreVersion
      ASPNETCORE_SHA512  = local.technitium.aspnetcoreSha512
      NAMESERVERS        = join(" ", var.guest.network.dns)
    }
    # The seals the container has opened live with its identity, which survives a new root disk.
    nonce_record = "/etc/dns/octopushy-seal-nonces"
  }

  # Octopushy sets this from provision.secrets, and only for the apply: it is null while
  # planning, and being ephemeral it stays out of the saved plan and the state as well.
  secrets = {
    admin_password = var.admin_password
  }
}

# This root was a single file of resources before lxc-app took them over; an existing state moves.
moved {
  from = tls_private_key.install
  to   = module.app.tls_private_key.install
}

moved {
  from = tls_private_key.seal
  to   = module.app.tls_private_key.seal
}

moved {
  from = module.container
  to   = module.app.module.container
}

moved {
  from = terraform_data.install
  to   = module.app.terraform_data.install
}
