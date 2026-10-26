locals {
  images_datastore = lookup(var.host.datastores, "images", "local")
  disks_datastore  = lookup(var.host.datastores, "disks", "local-lvm")
  system_disk      = var.guest.hardware.disks[0]
  data_disks       = slice(var.guest.hardware.disks, 1, length(var.guest.hardware.disks))
  static_address   = var.guest.network.mode == "static"
}

# The image is downloaded per guest, because each guest has its own state: a shared download
# would be owned by several states at once.
resource "proxmox_download_file" "image" {
  content_type       = "import"
  datastore_id       = local.images_datastore
  node_name          = var.guest.host
  url                = var.template.source.url
  checksum           = var.template.source.checksum
  checksum_algorithm = var.template.source.checksumAlgorithm
  file_name          = "${var.guest.id}-${var.template.source.fileName}"
}

resource "proxmox_virtual_environment_vm" "guest" {
  name        = var.guest.id
  description = "Managed by Octopushy as ${var.octopushy.address}.\nworker-generation: ${var.guest.workerGeneration}"
  tags        = sort(distinct(["octopushy", var.octopushy.lab]))
  node_name   = var.guest.host
  vm_id       = var.guest.vmid

  bios          = "ovmf"
  machine       = "q35"
  scsi_hardware = "virtio-scsi-single"
  on_boot       = true
  started       = true

  # Proxmox refuses to delete a protected VM. Octopushy lifts the protection only in an
  # approved retire run, right before it applies the destroy plan.
  protection      = true
  stop_on_destroy = true

  agent {
    enabled = true
    trim    = true
  }

  cpu {
    sockets = var.guest.hardware.sockets
    cores   = var.guest.hardware.cores
    type    = var.guest.hardware.cpuType
  }

  memory {
    dedicated = var.guest.hardware.memory
  }

  efi_disk {
    datastore_id = local.disks_datastore
    type         = "4m"
  }

  disk {
    interface    = "scsi0"
    datastore_id = coalesce(local.system_disk.datastore, local.disks_datastore)
    import_from  = proxmox_download_file.image.id
    size         = local.system_disk.size
    iothread     = true
    discard      = "on"
  }

  dynamic "disk" {
    for_each = { for index, disk in local.data_disks : "scsi${index + 1}" => disk }
    content {
      interface    = disk.key
      serial       = "opy-worker-state-v1"
      datastore_id = coalesce(disk.value.datastore, local.disks_datastore)
      size         = disk.value.size
      iothread     = true
      discard      = "on"
    }
  }

  initialization {
    datastore_id = local.disks_datastore

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
      username = var.admin_user
      keys     = var.guest.sshAuthorizedKeys
    }
  }

  network_device {
    bridge      = var.guest.network.bridge
    vlan_id     = var.guest.network.vlan
    mac_address = var.guest.network.mac
  }

  operating_system {
    type = "l26"
  }

  serial_device {}

  lifecycle {
    # A new template image changes the download, but an existing VM keeps its disk:
    # re-imaging a guest should be a deliberate recreate, not a side effect.
    ignore_changes = [disk[0].import_from]
  }
}
