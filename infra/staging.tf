resource "render_web_service" "staging_api" {
  environment_id    = render_project.orbit.environments["Staging"].id
  name              = "orbit-api-staging"
  plan              = "free"
  region            = "ohio"
  health_check_path = "/health"
  custom_domains    = length(var.staging_api_custom_domains) > 0 ? [for domain in var.staging_api_custom_domains : { name = domain }] : null

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
  environment_id    = render_project.orbit.environments["Staging"].id
  name              = "orbit-web-staging"
  plan              = "free"
  region            = "ohio"
  health_check_path = var.web_health_check_path
  custom_domains    = length(var.staging_web_custom_domains) > 0 ? [for domain in var.staging_web_custom_domains : { name = domain }] : null

  lifecycle {
    ignore_changes = [runtime_source.image.digest]
  }

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

output "staging_environment_id" {
  value = render_project.orbit.environments["Staging"].id
}

output "staging_api_service_id" {
  value = render_web_service.staging_api.id
}
