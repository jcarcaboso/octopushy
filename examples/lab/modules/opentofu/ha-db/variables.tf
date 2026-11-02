# Octopushy passes these in octopushy.auto.tfvars.json. The lxc-app and proxmox-lxc modules check
# what they use themselves; these are the settings this root needs on top of that.

variable "octopushy" {
  description = "The guest's address, its stack name, and the revision being planned."
  type        = any
}

variable "guest" {
  description = "The guest's identity, placement, hardware, network, and inputs from guest.yaml."
  type = object({
    id       = string
    vmid     = number
    host     = string
    hardware = any
    network = object({
      bridge  = string
      mode    = string
      address = string
      gateway = string
      vlan    = optional(number)
      mac     = optional(string)
      dns     = optional(list(string), [])
    })
    sshAuthorizedKeys = optional(list(string), [])
    inputs = object({
      startup = optional(object({
        order     = optional(number)
        upDelay   = optional(number)
        downDelay = optional(number)
      }))
      postgresql = object({
        # The only addresses pg_hba lets in, each as a single host.
        clients = list(string)
      })
      victoriametrics = optional(object({
        version   = optional(string, "1.155.0")
        sha256    = optional(string, "7180e65913d55ab99c0be8f4a3ad17a0fafe5598a21ff6327fecba3190d7b176")
        retention = optional(string, "2y")
      }), {})
      # The plan cannot see a secret, so changing one in the store starts nothing. Raising this
      # does: the install runs again and the databases take the passwords the store holds.
      credentialsRevision = optional(number, 1)
    })
  })

  validation {
    condition     = var.guest.network.mode == "static"
    error_message = "A database server needs a static address: set network.mode to static."
  }

  # The container itself is checked by lxc-app. These reach the install, as data, but the install
  # builds pg_hba.conf and download links from them, so they are only ever what they should be.
  validation {
    condition = (
      length(var.guest.inputs.postgresql.clients) > 0
      && alltrue([
        for client in var.guest.inputs.postgresql.clients :
        can(regex("^[0-9]{1,3}(\\.[0-9]{1,3}){3}$", client)) && can(cidrhost("${client}/32", 0))
      ])
    )
    error_message = "inputs.postgresql.clients must list at least one IPv4 address, like 10.15.40.10, and no networks."
  }

  validation {
    condition = (
      can(regex("^[0-9]+\\.[0-9]+\\.[0-9]+$", var.guest.inputs.victoriametrics.version))
      && can(regex("^[0-9a-f]{64}$", var.guest.inputs.victoriametrics.sha256))
      && can(regex("^[1-9][0-9]{0,2}[dwmy]$", var.guest.inputs.victoriametrics.retention))
    )
    error_message = "inputs.victoriametrics needs a version of numbers, a SHA-256 in lower-case hex, and a retention like 2y or 90d."
  }

  validation {
    condition     = var.guest.inputs.credentialsRevision >= 1 && floor(var.guest.inputs.credentialsRevision) == var.guest.inputs.credentialsRevision
    error_message = "inputs.credentialsRevision must be a whole number from 1."
  }

  # Everything is on the container's own disk, so a disk a mount point would not outlive it.
  validation {
    condition     = length(try(var.guest.hardware.mountPoints, [])) == 0
    error_message = "ha-db keeps its data on the root disk: declare no mount points."
  }
}

variable "host" {
  description = "The Proxmox host's settings from installation.yaml."
  type        = any
}

variable "template" {
  description = "The container template the root disk is created from."
  type        = any
}

# Octopushy sets these from provision.secrets, and only for the apply: they are null while
# planning, and being ephemeral they stay out of the saved plan and the state as well. The operator
# makes them and keeps a copy for Home Assistant: Octopushy never shows a stored secret again.
variable "postgres_password" {
  description = "The password of the homeassistant role in PostgreSQL."
  type        = string
  ephemeral   = true
  nullable    = true
  default     = null

  validation {
    condition     = var.postgres_password == null || can(regex("^[A-Za-z0-9!#%&()*+,./:;<=>?@^_{|}~-]{16,128}$", var.postgres_password))
    error_message = "The PostgreSQL password needs 16 to 128 characters from letters, digits and !#%&()*+,./:;<=>?@^_{|}~-, on one line."
  }
}

variable "victoriametrics_password" {
  description = "The password VictoriaMetrics asks for, for the user homeassistant."
  type        = string
  ephemeral   = true
  nullable    = true
  default     = null

  validation {
    condition     = var.victoriametrics_password == null || can(regex("^[A-Za-z0-9!#%&()*+,./:;<=>?@^_{|}~-]{16,128}$", var.victoriametrics_password))
    error_message = "The VictoriaMetrics password needs 16 to 128 characters from letters, digits and !#%&()*+,./:;<=>?@^_{|}~-, on one line."
  }
}
