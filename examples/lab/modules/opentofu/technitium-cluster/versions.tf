terraform {
  required_version = ">= 1.11.0"

  required_providers {
    technitium = {
      source  = "darkhonor/technitium"
      version = "1.3.0"
    }
  }
}

# The login comes from TECHNITIUM_USERNAME and TECHNITIUM_PASSWORD, which Octopushy sets for
# each attempt, so it enters neither a saved plan nor the state. server_url has to be here:
# the provider's schema requires it even though it also reads TECHNITIUM_SERVER_URL.
provider "technitium" {
  server_url      = "https://${var.cluster.primary.address}:53443"
  skip_tls_verify = !var.cluster.tls.verify
}
