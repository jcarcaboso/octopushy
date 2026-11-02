output "vm_id" {
  value = module.container.vm_id
}

output "ipv4_addresses" {
  description = "By interface."
  value       = module.container.ipv4_addresses
}

output "address" {
  description = "The container's static address, without its prefix."
  value       = local.address
}

output "incarnation" {
  description = "Changes whenever the container is replaced."
  value       = module.container.incarnation
}

output "installed" {
  description = "Set once the install has finished, so that whatever should wait for it can depend on this."
  value       = terraform_data.install.id
}
