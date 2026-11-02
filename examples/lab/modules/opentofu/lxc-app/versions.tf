terraform {
  # Ephemeral variables need 1.11.
  required_version = ">= 1.11.0"

  required_providers {
    proxmox = {
      source  = "bpg/proxmox"
      version = "~> 0.117.0"
    }
    tls = {
      source  = "hashicorp/tls"
      version = "~> 4.4.0"
    }
  }
}

# There is no provider block, so a recipe's root can use this as a module. The root configures
# the proxmox provider, from the environment Octopushy sets for each attempt (PROXMOX_VE_ENDPOINT,
# PROXMOX_VE_API_TOKEN, PROXMOX_VE_INSECURE), so the token never enters a saved plan.
