# Declares only part of what a stage is given (the service's inputs); OpenTofu warns about the rest.
variable "port" {
  type = number
}

resource "terraform_data" "rule" {
  input = var.port
}
