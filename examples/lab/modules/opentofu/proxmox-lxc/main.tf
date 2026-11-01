locals {
  images_datastore = lookup(var.host.datastores, "images", "local")
  disks_datastore  = lookup(var.host.datastores, "disks", "local-lvm")
  system_disk      = var.guest.hardware.disks[0]
  static_address   = var.guest.network.mode == "static"
  firewall         = var.guest.inputs.firewall
}

# The template is downloaded per guest, because each guest has its own state: a shared
# download would be owned by several states at once.
resource "proxmox_download_file" "template" {
  content_type       = "vztmpl"
  datastore_id       = local.images_datastore
  node_name          = var.guest.host
  url                = var.template.source.url
  checksum           = var.template.source.checksum
  checksum_algorithm = var.template.source.checksumAlgorithm
  file_name          = "${var.guest.id}-${var.template.source.fileName}"
}

resource "proxmox_virtual_environment_container" "guest" {
  description = "Managed by Octopushy as ${var.octopushy.address}."
  tags        = sort(distinct(["octopushy", var.octopushy.lab]))
  node_name   = var.guest.host
  vm_id       = var.guest.vmid

  unprivileged  = true
  started       = true
  start_on_boot = true

  # Proxmox refuses to delete a protected container. Octopushy lifts the protection only in
  # an approved retire run, right before it applies the destroy plan.
  protection = true

  features {
    # systemd needs this to set up the sandboxes of its services.
    nesting = true
    keyctl  = false
  }

  cpu {
    cores = var.guest.hardware.cores
  }

  memory {
    dedicated = var.guest.hardware.memory
    swap      = var.guest.hardware.swap
  }

  disk {
    datastore_id = coalesce(local.system_disk.datastore, local.disks_datastore)
    size         = local.system_disk.size
  }

  dynamic "mount_point" {
    for_each = var.guest.hardware.mountPoints
    content {
      path   = mount_point.value.path
      volume = coalesce(mount_point.value.volume, mount_point.value.datastore, local.disks_datastore)
      size   = mount_point.value.size == null ? null : "${mount_point.value.size}G"
      backup = mount_point.value.backup
    }
  }

  initialization {
    hostname = var.guest.id

    dynamic "dns" {
      for_each = length(var.guest.network.dns) == 0 ? [] : [var.guest.network.dns]
      content {
        servers = dns.value
      }
    }

    ip_config {
      ipv4 {
        address = local.static_address ? var.guest.network.address : "dhcp"
        gateway = local.static_address ? var.guest.network.gateway : null
      }
    }

    user_account {
      keys = concat(var.guest.sshAuthorizedKeys, [for key in var.extra_ssh_keys : trimspace(key)])
    }
  }

  network_interface {
    name        = "eth0"
    bridge      = var.guest.network.bridge
    vlan_id     = var.guest.network.vlan
    mac_address = var.guest.network.mac
    firewall    = local.firewall.enabled
  }

  operating_system {
    template_file_id = proxmox_download_file.template.id
    type             = coalesce(var.template.os, "unmanaged")
  }

  dynamic "startup" {
    for_each = var.guest.inputs.startup == null ? [] : [var.guest.inputs.startup]
    content {
      order      = startup.value.order
      up_delay   = startup.value.upDelay
      down_delay = startup.value.downDelay
    }
  }

  wait_for_ip {
    ipv4 = true
  }

  lifecycle {
    # A new template changes the download, but an existing container keeps its disk:
    # re-imaging a guest should be a deliberate recreate, not a side effect. Proxmox reads
    # the keys only when it creates the container.
    ignore_changes = [operating_system[0].template_file_id, initialization[0].user_account]
  }
}

# The container's id is its VMID, which stays the same when it is replaced. This one changes.
resource "terraform_data" "incarnation" {
  lifecycle {
    replace_triggered_by = [proxmox_virtual_environment_container.guest.id]
  }
}

# Inert until the datacenter firewall is enabled, however these are set.
resource "proxmox_virtual_environment_firewall_options" "guest" {
  count = local.firewall.enabled ? 1 : 0

  node_name     = var.guest.host
  container_id  = proxmox_virtual_environment_container.guest.vm_id
  enabled       = true
  input_policy  = "DROP"
  output_policy = "ACCEPT"
  dhcp          = !local.static_address
}

resource "proxmox_virtual_environment_firewall_rules" "guest" {
  count = local.firewall.enabled ? 1 : 0

  node_name    = var.guest.host
  container_id = proxmox_virtual_environment_container.guest.vm_id

  dynamic "rule" {
    for_each = local.firewall.rules
    content {
      type    = "in"
      action  = "ACCEPT"
      proto   = rule.value.proto
      dport   = rule.value.dport
      source  = rule.value.source
      comment = rule.value.comment
    }
  }
}
