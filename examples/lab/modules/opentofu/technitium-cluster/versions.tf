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
# each attempt, so it enters neither a saved plan nor the state. TECHNITIUM_CACERT names the
# certificate an operator trusted for the primary, the only one this will talk to. server_url
# has to be here: the schema requires it even though the provider also reads the environment.
provider "technitium" {
  server_url = "https://${var.cluster.primary.address}:53443"
}
