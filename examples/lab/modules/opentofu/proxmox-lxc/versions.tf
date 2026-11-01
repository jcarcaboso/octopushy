terraform {
  required_version = ">= 1.10.0"

  required_providers {
    proxmox = {
      source  = "bpg/proxmox"
      version = "~> 0.117.0"
    }
  }
}

# There is no provider block, so technitium-lxc can use this root as a module. As a root it
# gets an empty configuration, filled from the environment Octopushy sets for each attempt
# (PROXMOX_VE_ENDPOINT, PROXMOX_VE_API_TOKEN, PROXMOX_VE_INSECURE), so the token never enters
# a saved plan.
