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

output "api_endpoint" {
  description = "Technitium's HTTPS API, with a self-signed certificate."
  value       = "https://${local.address}:53443"
  depends_on  = [terraform_data.install]
}

output "technitium_version" {
  value      = local.technitium.version
  depends_on = [terraform_data.install]
}
