output "vm_id" {
  value = proxmox_virtual_environment_container.guest.vm_id
}

output "ipv4_addresses" {
  description = "By interface."
  value       = proxmox_virtual_environment_container.guest.ipv4
}

output "incarnation" {
  description = "Changes whenever the container is replaced."
  value       = terraform_data.incarnation.id
}
