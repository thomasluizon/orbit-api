variable "render_owner_id" {
  type    = string
  default = "tea-ctg9ljtumphs73dep1o0"
}

variable "staging_environment_id" {
  type = string
}

variable "staging_api_service_id" {
  type = string
}

variable "staging_postgres_ip_allow_list" {
  description = "Initial staging Postgres addresses at creation. Later allow list changes are managed through the Render API outside Terraform."
  type = list(object({
    cidr_block  = string
    description = string
  }))
  default = []
}
