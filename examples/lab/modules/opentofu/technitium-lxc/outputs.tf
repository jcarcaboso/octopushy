output "vm_id" {
  value = module.app.vm_id
}

output "ipv4_addresses" {
  description = "By interface."
  value       = module.app.ipv4_addresses
}

output "address" {
  description = "The container's static address, without its prefix."
  value       = module.app.address
}

output "api_endpoint" {
  description = "Technitium's HTTPS API, with a self-signed certificate."
  value       = "https://${module.app.address}:53443"
  depends_on  = [module.app]
}

output "technitium_version" {
  value      = local.technitium.version
  depends_on = [module.app]
}
