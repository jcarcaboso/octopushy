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

output "postgresql" {
  description = "Where Home Assistant's recorder connects; the password is the one in the store."
  value       = "postgresql://homeassistant@${module.app.address}/homeassistant"
  depends_on  = [module.app]
}

output "victoriametrics" {
  description = "Where Home Assistant's InfluxDB integration writes; the user is homeassistant and the password is the one in the store."
  value       = "http://${module.app.address}:8428"
  depends_on  = [module.app]
}
