locals {
  blocking = var.cluster.settings.blocking
  zones    = { for zone in var.cluster.settings.forwardZones : zone.zone => zone }
  catalog  = "cluster-catalog.${var.cluster.domain}"
}

# Forms the cluster on the primary. It holds no password: the provider's own login does this.
# force_delete stays unset, and Octopushy refuses a change of primary or domain before planning.
resource "technitium_cluster" "this" {
  cluster_domain            = var.cluster.domain
  primary_node_ip_addresses = [var.cluster.primary.address]

  lifecycle {
    prevent_destroy = true
  }
}

# Records that a secondary belongs here; the agent's technitium executor does the join. The
# provider's technitium_cluster_secondary would need the password in primary_node_password,
# an ordinary sensitive attribute, which puts it in the saved plan and the state.
#
# Removing one is a delete in the plan, so policy.destructive applies and the preview shows it.
resource "terraform_data" "secondary" {
  for_each = var.cluster.secondaries

  input = each.value.address
}

# Replaced when the agent finds blocked or allowed entries nobody declared, so that the plan
# shows them going and they follow approval. The provider reads only the entries it knows.
resource "terraform_data" "undeclared_entries" {
  input = "removed by the agent after the apply"
}

# The provider's defaults are not Technitium's, so everything it would change is set here:
# randomize_name, log_queries, logging_type, and forwarder_protocol would all differ.
resource "technitium_server_settings" "this" {
  depends_on = [technitium_cluster.this]

  recursion             = var.cluster.settings.recursion.policy
  recursion_network_acl = var.cluster.settings.recursion.networks

  dnssec_validation  = true
  qname_minimization = true
  randomize_name     = false
  serve_stale        = true

  # Per node: only the primary would receive these, so they keep Technitium's own defaults.
  log_queries       = false
  logging_type      = "File"
  max_log_file_days = 365

  enable_dns_over_tls   = false
  enable_dns_over_https = false

  enable_blocking                  = local.blocking.enabled
  blocking_type                    = local.blocking.type
  block_list_update_interval_hours = local.blocking.updateIntervalHours
  block_list_urls                  = local.blocking.lists
  # Verification proves a name is blocked from the extended DNS error that goes with this.
  allow_txt_blocking_report = true
}

# The provider drops either resource from its state when it finds no entry, so an empty list
# has to be no resource at all, or every plan would create it again.
resource "technitium_blocked_zones" "this" {
  count      = length(local.blocking.blocked) > 0 ? 1 : 0
  depends_on = [technitium_cluster.this]

  domains = local.blocking.blocked
}

resource "technitium_allowed_zones" "this" {
  count      = length(local.blocking.allowed) > 0 ? 1 : 0
  depends_on = [technitium_cluster.this]

  domains = local.blocking.allowed
}

resource "technitium_zone" "forward" {
  for_each   = local.zones
  depends_on = [technitium_cluster.this]

  name = each.key
  type = "Forwarder"
}

resource "technitium_record" "forward" {
  for_each = local.zones

  zone     = technitium_zone.forward[each.key].name
  name     = each.key
  type     = "FWD"
  value    = each.value.forwarder
  protocol = each.value.protocol
  ttl      = 3600

  forwarder_priority = 0
  dnssec_validation  = false
}

# Only zones in the catalog reach the secondaries.
resource "technitium_catalog_membership" "forward" {
  for_each   = local.zones
  depends_on = [technitium_record.forward]

  zone         = technitium_zone.forward[each.key].name
  catalog_zone = local.catalog
}
