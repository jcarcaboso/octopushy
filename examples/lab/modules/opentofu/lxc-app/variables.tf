# What a recipe's root passes: the values Octopushy gave it, and what it installs. See
# docs/lxc-recipes.md for how a recipe is written.

variable "octopushy" {
  description = "The guest's address, its stack name, and the revision being planned."
  type        = any
}

variable "host" {
  description = "The Proxmox host's settings from installation.yaml."
  type        = any
}

variable "template" {
  description = "The container template the root disk is created from."
  type        = any
}

variable "guest" {
  description = "The guest's identity, placement, hardware, and network from guest.yaml. Recipe inputs other than startup are not passed on."
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
    inputs = optional(object({
      startup = optional(object({
        order     = optional(number)
        upDelay   = optional(number)
        downDelay = optional(number)
      }))
    }), {})
  })

  # OpenTofu installs over SSH, so the address has to be known without asking Proxmox. It also
  # reaches the install, and the seal that carries the secrets is bound to it.
  validation {
    condition     = var.guest.network.mode == "static"
    error_message = "An installed container needs a static address: set network.mode to static."
  }

  validation {
    condition = (
      can(regex("^[0-9]{1,3}(\\.[0-9]{1,3}){3}/[0-9]{1,2}$", var.guest.network.address))
      && can(cidrhost(var.guest.network.address, 0))
    )
    error_message = "network.address must be an IPv4 address with its prefix length, like 10.1.0.32/24."
  }
}

variable "firewall" {
  description = "Proxmox firewall rules for the container, with everything else dropped. Inert unless the datacenter firewall is enabled, and unless enabled."
  type = object({
    enabled = optional(bool, false)
    rules = optional(list(object({
      proto   = string
      dport   = string
      source  = optional(string)
      comment = optional(string)
    })), [])
  })
  default = {}
}

variable "app" {
  description = "What to run in the container: a directory, a script in it, and the values it needs."
  type = object({
    # Uploaded as one directory. Everything in it is part of what an operator reviews.
    files = string
    # The file in that directory that is run, as root.
    script = string
    # Not secret. Written to a file the install reads as NAME=value lines and never evaluates.
    environment = optional(map(string), {})
    # Values whose change must install again, such as a revision that stands for a secret the
    # plan cannot see. Files and environment already do.
    reinstall_on = optional(map(string), {})
    # Where the guest remembers the seals it has opened. It has to survive an install again:
    # a recipe with a volume of its own can put it there.
    nonce_record = optional(string, "/var/lib/octopushy-install/used-nonces")
  })

  validation {
    condition     = can(regex("^[A-Za-z0-9][A-Za-z0-9._-]*$", var.app.script)) && fileexists("${var.app.files}/${var.app.script}")
    error_message = "app.script must be the name of a file directly in app.files, using letters, digits, dots, hyphens and underscores."
  }

  # Names may not reach the shell's own settings, and OCTOPUSHY_ is the module's.
  validation {
    condition = alltrue([
      for name in keys(var.app.environment) :
      can(regex("^[A-Z][A-Z0-9_]*$", name))
      && !can(regex("^(OCTOPUSHY_|LD_|BASH|PATH$|IFS$|ENV$|SHELLOPTS$|PS[0-9]$|PROMPT_COMMAND$|CDPATH$|GLOBIGNORE$|HOME$|SHELL$|TMPDIR$)", name))
    ])
    error_message = "app.environment names must match ^[A-Z][A-Z0-9_]*$ and must not be OCTOPUSHY_*, LD_*, BASH*, or a name the shell uses itself."
  }

  # One line each: a value is data, and a newline would be a second entry.
  validation {
    condition     = alltrue([for value in values(var.app.environment) : !can(regex("[\\x00-\\x1f\\x7f]", value))])
    error_message = "app.environment values must be one line, with no control characters. Secrets belong in the secrets variable."
  }

  validation {
    condition     = can(regex("^/[A-Za-z0-9._/-]+$", var.app.nonce_record))
    error_message = "app.nonce_record must be an absolute path of letters, digits, dots, slashes, hyphens and underscores."
  }
}

# A recipe's root fills this from its own ephemeral variables, one for each secret Octopushy
# binds (provision.secrets). Every value is null while planning, and being ephemeral, none of it
# is in the saved plan or the state: it exists only while the apply runs.
variable "secrets" {
  description = "Secrets by name. They reach the container sealed, and the script finds each in $OCTOPUSHY_SECRETS/<name>."
  type        = map(string)
  ephemeral   = true
  default     = {}

  validation {
    condition     = alltrue([for name in keys(var.secrets) : can(regex("^[a-z][a-z0-9_]*$", name))])
    error_message = "A secret's name must match ^[a-z][a-z0-9_]*$: it becomes a file name."
  }
}
