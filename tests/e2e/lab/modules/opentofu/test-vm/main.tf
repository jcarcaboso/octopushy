# Declares only part of what Octopushy passes; the rest is dropped on conversion.
variable "octopushy" {
  type = object({
    address = string
    stack   = string
  })
}

variable "guest" {
  type = object({
    id   = string
    vmid = number
    host = string
    hardware = object({
      cores  = number
      memory = number
    })
  })
}

resource "terraform_data" "vm" {
  input = {
    address = var.octopushy.address
    vmid    = var.guest.vmid
    host    = var.guest.host
    cores   = var.guest.hardware.cores
    memory  = var.guest.hardware.memory
  }
}

output "vm_id" {
  value = var.guest.vmid
}
