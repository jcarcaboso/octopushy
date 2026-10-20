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
  description = "The guest's identity, placement, hardware, and network from guest.yaml."
  type = object({
    id   = string
    vmid = number
    host = string
    hardware = object({
      cores   = number
      memory  = number
      cpuType = optional(string, "x86-64-v2-AES")
      disks = list(object({
        size      = number
        datastore = optional(string)
      }))
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
  })

  validation {
    condition     = length(var.guest.hardware.disks) > 0
    error_message = "Declare at least the system disk under hardware.disks."
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
  description = "The cloud image imported as the system disk."
  type = object({
    id = string
    source = object({
      url               = string
      checksum          = string
      checksumAlgorithm = string
      fileName          = string
    })
  })
  nullable = false
}

variable "admin_user" {
  description = "The user cloud-init creates with the guest's SSH keys."
  type        = string
  default     = "ops"
}
