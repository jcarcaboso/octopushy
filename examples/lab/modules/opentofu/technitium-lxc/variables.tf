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
      dns     = list(string)
    })
    sshAuthorizedKeys = optional(list(string), [])
    inputs = optional(object({
      startup = optional(object({
        order     = optional(number)
        upDelay   = optional(number)
        downDelay = optional(number)
      }))
      technitium = optional(object({
        version           = optional(string, "15.6.2")
        sha256            = optional(string, "51f2d4ebaf156a85035e820afde5e1d428f1920a37d7f0c65203fb1766917e08")
        aspnetcoreVersion = optional(string, "10.0.14")
        aspnetcoreSha512  = optional(string, "dd024fd2616926a555b75cbf7a836f6dce998199f0596d9d75676dca2b9d59c8ee8673c6ac23473c0f1a4bdd59eb5cd02fe9ab097eeaf2dd8c8bd117c85e7031")
      }), {})
      firewall = optional(object({
        enabled      = optional(bool, false)
        dnsSources   = optional(list(string), [])
        adminSources = optional(list(string), [])
      }), {})
    }), {})
  })

  validation {
    condition     = var.guest.network.mode == "static"
    error_message = "A DNS server needs a static address: set network.mode to static."
  }

  # The address reaches the install's shell and its certificate, so it is only ever an address.
  validation {
    condition = (
      can(regex("^[0-9]{1,3}(\\.[0-9]{1,3}){3}/[0-9]{1,2}$", var.guest.network.address))
      && can(cidrhost(var.guest.network.address, 0))
    )
    error_message = "network.address must be an IPv4 address with its prefix length, like 10.1.0.32/24."
  }

  validation {
    condition = alltrue([
      for server in var.guest.network.dns :
      server != split("/", var.guest.network.address)[0] && !startswith(server, "127.")
      && can(regex("^[0-9a-fA-F.:]+$", server))
    ]) && length(var.guest.network.dns) > 0
    error_message = "network.dns must name other resolvers: the container must not resolve through itself."
  }

  validation {
    condition     = contains([for mount in var.guest.hardware.mountPoints : mount.path], "/etc/dns")
    error_message = "Declare a mount point for /etc/dns: it holds the server's identity."
  }

  # These reach the install's command line.
  validation {
    condition = (
      can(regex("^[0-9a-f]{64}$", var.guest.inputs.technitium.sha256))
      && can(regex("^[0-9a-f]{128}$", var.guest.inputs.technitium.aspnetcoreSha512))
      && can(regex("^[0-9]+\\.[0-9]+\\.[0-9]+$", var.guest.inputs.technitium.aspnetcoreVersion))
    )
    error_message = "inputs.technitium needs a version of numbers, and checksums in lower-case hex."
  }

  # 15.6.2 fixes cluster configuration sync.
  validation {
    condition = can(regex("^[0-9]+\\.[0-9]+(\\.[0-9]+)?$", var.guest.inputs.technitium.version)) && try(
      sum([for index, part in split(".", var.guest.inputs.technitium.version) : tonumber(part) * [1000000, 1000, 1][index]]) >= 15006002,
      false
    )
    error_message = "inputs.technitium.version must be at least 15.6.2."
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

# Octopushy sets this from provision.secrets, and only for the apply: it is null while
# planning, and being ephemeral it stays out of the saved plan and the state as well.
variable "admin_password" {
  description = "The password Technitium's admin user gets on its first start."
  type        = string
  ephemeral   = true
  nullable    = true
  default     = null

  validation {
    condition = var.admin_password == null || (
      length(var.admin_password) >= 12 && !can(regex("[\\r\\n]", var.admin_password))
    )
    error_message = "The admin password needs at least 12 characters on one line."
  }
}
