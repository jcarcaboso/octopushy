# Octopushy passes these in octopushy.auto.tfvars.json. Attributes not declared here, such
# as the guest's profiles, are dropped when the values are converted.

variable "octopushy" {
  description = "The guest's address, its stack name, and the revision being planned."
  type = object({
    address      = string
    stack        = string
    lab          = string
    installation = string
    revision     = string
  })
}

variable "guest" {
  description = "The guest's identity, placement, hardware, network, and inputs from guest.yaml."
  type = object({
    id   = string
    vmid = number
    host = string
    hardware = object({
      cores  = number
      memory = number
      swap   = optional(number, 0)
      disks = list(object({
        size      = number
        datastore = optional(string)
      }))
      mountPoints = optional(list(object({
        path      = string
        size      = optional(number)
        datastore = optional(string)
        volume    = optional(string)
        backup    = optional(bool, true)
      })), [])
    })
    network = object({
      bridge  = string
      mode    = optional(string, "dhcp")
      address = optional(string)
      gateway = optional(string)
      vlan    = optional(number)
      mac     = optional(string)
      dns     = optional(list(string), [])
    })
    sshAuthorizedKeys = optional(list(string), [])
    inputs = optional(object({
      startup = optional(object({
        order     = optional(number)
        upDelay   = optional(number)
        downDelay = optional(number)
      }))
      firewall = optional(object({
        enabled = optional(bool, false)
        rules = optional(list(object({
          proto   = string
          dport   = string
          source  = optional(string)
          comment = optional(string)
        })), [])
      }), {})
    }), {})
  })

  validation {
    condition     = length(var.guest.hardware.disks) == 1
    error_message = "A container has one root disk under hardware.disks; declare the others as hardware.mountPoints."
  }
}

variable "host" {
  description = "The Proxmox host's settings from installation.yaml."
  type = object({
    id         = string
    datastores = map(string)
  })
}

variable "template" {
  description = "The container template the root disk is created from."
  type = object({
    id = string
    os = optional(string)
    source = object({
      url               = string
      checksum          = string
      checksumAlgorithm = string
      fileName          = string
    })
  })
  nullable = false

  validation {
    condition     = can(regex("\\.tar\\.(gz|xz|zst)$", var.template.source.fileName))
    error_message = "A container needs a template archive (.tar.gz, .tar.xz, or .tar.zst), not a disk image."
  }
}

variable "extra_ssh_keys" {
  description = "Public keys root accepts besides sshAuthorizedKeys, such as a stack's own."
  type        = list(string)
  default     = []
}
