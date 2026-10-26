output "vm_id" {
  value = proxmox_virtual_environment_vm.guest.vm_id
}

output "ipv4_addresses" {
  value = proxmox_virtual_environment_vm.guest.ipv4_addresses
}

output "mac_addresses" {
  value = proxmox_virtual_environment_vm.guest.mac_addresses
}
