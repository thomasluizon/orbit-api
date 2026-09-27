resource "render_postgres" "staging" {
  name                      = "orbit-staging"
  database_name             = "orbit_staging"
  database_user             = "orbit_staging"
  plan                      = "free"
  region                    = "ohio"
  version                   = "17"
  high_availability_enabled = false
}

resource "render_web_service" "staging_api" {
  name              = "orbit-api-staging"
  plan              = "free"
  region            = "ohio"
  health_check_path = "/health"
  custom_domains    = [for domain in var.staging_api_custom_domains : { name = domain }]

  runtime_source = {
    docker = {
      branch          = "redesign/main"
      repo_url        = "https://github.com/thomasluizon/orbit-api"
      dockerfile_path = "./Dockerfile"
      context         = "."
      auto_deploy     = true
    }
  }
}

resource "render_web_service" "staging_web" {
  name              = "orbit-web-staging"
  plan              = "free"
  region            = "ohio"
  health_check_path = var.web_health_check_path
  custom_domains    = [for domain in var.staging_web_custom_domains : { name = domain }]

  runtime_source = {
    image = {
      image_url = "ghcr.io/thomasluizon/orbit-web"
      digest    = var.staging_web_digest
    }
  }
}

resource "render_env_group_link" "staging_api" {
  env_group_id = render_env_group.staging_api.id
  service_ids  = [render_web_service.staging_api.id]
}

resource "render_env_group_link" "staging_web" {
  env_group_id = render_env_group.staging_web.id
  service_ids  = [render_web_service.staging_web.id]
}
