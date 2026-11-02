locals {
  address = split("/", var.guest.network.address)[0]
  work    = "/run/octopushy-install"
  files   = trimsuffix(var.app.files, "/")

  # OpenTofu can check SSH's host key only against a public key, and nothing can give it this
  # container's before it connects, so whoever answers on the network gets what is uploaded.
  # The secrets are therefore sealed to a key that reaches the container only as the comment of an
  # SSH key in Proxmox's API, and never over SSH. This is a hand-built seal, because OpenTofu has
  # no encryption function; docs/lxc-recipes.md and docs/technitium-lxc.md say what it is worth.
  bootstrap     = sha256("octopushy-bootstrap:${tls_private_key.seal.private_key_openssh}")
  bootstrap_key = "${trimspace(tls_private_key.install.public_key_openssh)} octopushy-bootstrap=${local.bootstrap}"
  seal_alphabet = split("", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/")
  # One key for each job, so neither can stand in for the other.
  seal_encrypt      = sha256("octopushy-seal-encrypt:${local.bootstrap}")
  seal_authenticate = sha256("octopushy-seal-authenticate:${local.bootstrap}")
  seal_identity     = "${var.guest.id}/${local.address}"

  # NAME=value lines, which the install reads and never evaluates. The OCTOPUSHY_ ones are this
  # module's, and the recipe cannot set them.
  environment = merge(var.app.environment, {
    OCTOPUSHY_SCRIPT  = var.app.script
    OCTOPUSHY_ADDRESS = local.address
    OCTOPUSHY_NONCES  = var.app.nonce_record
  })
  environment_file = "${join("\n", [for name in sort(keys(local.environment)) : "${name}=${local.environment[name]}"])}\n"

  file_hashes = { for name in fileset(local.files, "**") : name => filesha256("${local.files}/${name}") }
}

# Lets OpenTofu reach the container, whose template has no way in otherwise. Proxmox has no
# user-data or exec for LXC. The private key lives in this guest's state.
resource "tls_private_key" "install" {
  algorithm = "ED25519"
}

# Used for its randomness only: the sealing key has nothing to do with the key SSH uses.
resource "tls_private_key" "seal" {
  algorithm = "ED25519"
}

module "container" {
  source = "../proxmox-lxc"

  octopushy      = var.octopushy
  host           = var.host
  template       = var.template
  extra_ssh_keys = [local.bootstrap_key]

  guest = merge(var.guest, {
    inputs = {
      startup  = var.guest.inputs.startup
      firewall = var.firewall
    }
  })
}

# A provisioner only runs when its resource is created, so what should install again replaces
# this resource. A failed install taints it, so the next apply tries again.
resource "terraform_data" "install" {
  depends_on = [module.container]

  triggers_replace = {
    container    = module.container.incarnation
    files        = sha256(jsonencode(local.file_hashes))
    environment  = sha256(local.environment_file)
    reinstall_on = sha256(jsonencode(var.app.reinstall_on))
    runner       = filesha256("${path.module}/guest/run.sh")
  }

  connection {
    type        = "ssh"
    host        = local.address
    user        = "root"
    private_key = tls_private_key.install.private_key_openssh
    timeout     = "5m"
  }

  # Nothing from a variable reaches a command: these are constants, and everything else
  # travels as a file.
  provisioner "remote-exec" {
    inline = ["rm -rf ${local.work} && install -d -m 0700 ${local.work} ${local.work}/app"]
  }

  # The trailing slash puts what is in the directory directly into the destination.
  provisioner "file" {
    source      = "${local.files}/"
    destination = "${local.work}/app"
  }

  provisioner "file" {
    source      = "${path.module}/guest/run.sh"
    destination = "${local.work}/run.sh"
  }

  provisioner "file" {
    content     = local.environment_file
    destination = "${local.work}/environment"
  }

  # nonce.cipher.tag, of all the secrets at once, because a provisioner cannot be repeated for
  # each. The nonce is random and made here, so that every upload has its own, and a retry after
  # a failed install does too. The secrets are name:base64(value) lines, so that a newline or a
  # colon in one cannot be taken for another entry, and the whole is base64 again because the
  # cipher works on that alphabet. This is evaluated only for the apply, and a secret that is
  # still null then fails it. cipher adds, mod 64, the base64 of those lines to a keystream
  # of SHA-256 blocks under the key and nonce; each block gives 40 of its 43 full characters.
  # tag covers this guest, the nonce and the cipher, and is hashed twice so that it cannot be
  # extended. run.sh checks the tag before it opens anything. With no secrets, nothing is sent.
  provisioner "file" {
    content = length(var.secrets) == 0 ? "" : one([for nonce in [uuid()] : one([for cipher in [join("", [
      for i, c in split("", replace(base64encode(join("\n", [for name in sort(keys(var.secrets)) : "${name}:${base64encode(var.secrets[name])}"])), "=", "")) :
      local.seal_alphabet[(index(local.seal_alphabet, c) + index(local.seal_alphabet,
      substr(base64sha256("${local.seal_encrypt}:${nonce}:${floor(i / 40)}"), i % 40, 1))) % 64]
      ])] :
      "${nonce}.${cipher}.${sha256("${local.seal_authenticate}:${sha256("${local.seal_authenticate}:${local.seal_identity}:${nonce}:${cipher}")}")}"
    ])])
    destination = "${local.work}/secrets.sealed"
  }

  provisioner "remote-exec" {
    inline = ["bash ${local.work}/run.sh"]
  }
}
