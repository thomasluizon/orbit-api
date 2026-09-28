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
  description = "Operator addresses allowed to connect to staging Postgres. Copy the live Render list into a local variable before planning."
  type = list(object({
    cidr_block  = string
    description = string
  }))
  default = []
}
