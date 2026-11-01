output "cluster_domain" {
  description = "The domain the cluster was formed with."
  value       = technitium_cluster.this.cluster_domain
}

output "primary" {
  description = "The primary's name in the cluster."
  value       = technitium_cluster.this.dns_server_domain
}
