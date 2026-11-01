variable "octopushy" {
  description = "Where this stack comes from, for reading a plan."
  type = object({
    address  = string
    stack    = string
    lab      = string
    cluster  = string
    revision = string
  })
}

variable "cluster" {
  description = "cluster.yaml, with each member's address filled in from its guest."
  type = object({
    id     = string
    domain = string
    tls    = object({ verify = bool })
    primary = object({
      guest   = string
      address = string
    })
    # Keyed by guest, such as moraband/mrb-dns-01.
    secondaries = map(object({ address = string }))
    settings = object({
      recursion = object({
        policy   = string
        networks = list(string)
      })
      forwardZones = list(object({
        zone      = string
        forwarder = string
        protocol  = string
      }))
      blocking = object({
        enabled             = bool
        type                = string
        updateIntervalHours = number
        lists               = list(string)
        blocked             = list(string)
        allowed             = list(string)
      })
    })
  })
}
