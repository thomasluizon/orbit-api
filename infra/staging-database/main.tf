resource "render_postgres" "staging" {
  environment_id            = var.staging_environment_id
  name                      = "orbit-staging"
  database_name             = "orbit_staging"
  database_user             = "orbit_staging"
  ip_allow_list             = var.staging_postgres_ip_allow_list
  plan                      = "free"
  region                    = "ohio"
  version                   = "17"
  high_availability_enabled = false

  lifecycle {
    ignore_changes = [database_name, database_user]
  }
}

locals {
  staging_db_host = regex("@([^:/]+)", render_postgres.staging.connection_info.internal_connection_string)[0]
  staging_db_name = regex("^[^:]+://[^/]+/([^/?]+)", render_postgres.staging.connection_info.internal_connection_string)[0]
  staging_npgsql  = "Host=${local.staging_db_host};Port=5432;Database=${local.staging_db_name};Username=${render_postgres.staging.database_user};Password=\"${replace(render_postgres.staging.connection_info.password, "\"", "\"\"")}\""
}

resource "render_env_group" "staging_database" {
  name = "orbit-staging-database"
  env_vars = {
    ConnectionStrings__DefaultConnection = { value = local.staging_npgsql }
    ConnectionStrings__SessionConnection = { value = local.staging_npgsql }
  }
}

resource "render_env_group_link" "staging_database" {
  env_group_id = render_env_group.staging_database.id
  service_ids  = [var.staging_api_service_id]
}

output "staging_external_connection_string" {
  value     = render_postgres.staging.connection_info.external_connection_string
  sensitive = true
}

output "staging_external_host" {
  value     = regex("@([^:/]+)", render_postgres.staging.connection_info.external_connection_string)[0]
  sensitive = true
}

output "staging_postgres_id" {
  value = render_postgres.staging.id
}
