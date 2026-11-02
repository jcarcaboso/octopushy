locals {
  victoriametrics = var.guest.inputs.victoriametrics
}

# The container, its SSH access, and running files/install.sh in it with the passwords sealed to
# it. lxc-app does all of that; docs/lxc-recipes.md says how. What this root adds is the settings of
# PostgreSQL and VictoriaMetrics, and the guard that keeps their data.
module "app" {
  source = "../lxc-app"

  octopushy = var.octopushy
  host      = var.host
  template  = var.template
  guest     = var.guest

  app = {
    files  = "${path.module}/files"
    script = "install.sh"
    # The script reads these as data.
    environment = {
      PG_CLIENTS   = join(" ", var.guest.inputs.postgresql.clients)
      VM_VERSION   = local.victoriametrics.version
      VM_SHA256    = local.victoriametrics.sha256
      VM_RETENTION = local.victoriametrics.retention
    }
    # The passwords are not in the plan, so this stands for them: raise it, and both are applied.
    reinstall_on = {
      credentialsRevision = tostring(var.guest.inputs.credentialsRevision)
    }
  }

  secrets = {
    postgres_password        = var.postgres_password
    victoriametrics_password = var.victoriametrics_password
  }
}

# Proxmox deletes a container's volumes when it deletes the container, and a mount point of its own
# goes with it. Everything here is on the root disk, so a plan that would replace or destroy the
# container would take the data. This stops such a plan at the plan, before anyone is asked to
# approve it: OpenTofu refuses it, naming this resource. It is created only after the install has
# finished, so a container whose first install failed can still be retried or replaced.
#
# To retire the guest, or to replace the container on purpose, remove this resource in a commit of
# its own and apply that first (docs/lxc-recipes.md, "Stateful guests"). prevent_destroy has to be a
# literal, so this cannot be a variable.
resource "terraform_data" "keep_data" {
  triggers_replace = module.app.incarnation
  depends_on       = [module.app]

  lifecycle {
    prevent_destroy = true
  }
}
