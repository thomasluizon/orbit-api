import {
  to = render_web_service.production_api
  id = "srv-d6tc2isr85hc739bf75g"
}

resource "render_web_service" "production_api" {
  environment_id    = render_project.orbit.environments["Production"].id
  name              = "orbit-api"
  plan              = "starter"
  region            = "ohio"
  health_check_path = "/health"
  env_vars          = { ORBIT_TERRAFORM_ENV_GROUP = { value = "production-api" } }
  custom_domains    = [{ name = "api.useorbit.org" }]

  lifecycle {
    # The imported service's own variables stay until the linked group is verified live, so the first apply cannot strip production's configuration.
    ignore_changes = [env_vars]
  }

  runtime_source = {
    docker = {
      branch          = "main"
      repo_url        = "https://github.com/thomasluizon/orbit-api"
      dockerfile_path = "./Dockerfile"
      context         = "."
      auto_deploy     = var.api_auto_deploy
    }
  }
}

resource "render_postgres" "production" {
  environment_id            = render_project.orbit.environments["Production"].id
  name                      = "orbit-production"
  database_name             = "orbit_production"
  database_user             = "orbit_production"
  plan                      = "basic_1gb"
  region                    = "ohio"
  version                   = "17"
  high_availability_enabled = false
}

resource "render_web_service" "production_web" {
  environment_id    = render_project.orbit.environments["Production"].id
  name              = "orbit-web"
  plan              = "0.5c-512mb"
  region            = "ohio"
  health_check_path = var.web_health_check_path
  custom_domains    = length(var.web_custom_domains) > 0 ? [for domain in var.web_custom_domains : { name = domain }] : null

  runtime_source = {
    image = {
      image_url = "ghcr.io/thomasluizon/orbit-web"
      digest    = var.production_web_digest
    }
  }
}

resource "render_static_site" "landing" {
  environment_id = render_project.orbit.environments["Production"].id
  name           = "orbit-landing"
  repo_url       = "https://github.com/thomasluizon/orbit-landing-page"
  branch         = "main"
  build_command  = "npm ci && npm run build"
  publish_path   = "dist"
  auto_deploy    = false
  custom_domains = length(var.landing_custom_domains) > 0 ? [for domain in var.landing_custom_domains : { name = domain }] : null

  headers = [
    { path = "/", name = "Link", value = "<https://useorbit.org/>; rel=\"canonical\"" },
    { path = "/*", name = "X-Content-Type-Options", value = "nosniff" },
    { path = "/*", name = "X-Frame-Options", value = "DENY" },
    { path = "/*", name = "Referrer-Policy", value = "strict-origin-when-cross-origin" },
    { path = "/*", name = "Permissions-Policy", value = "camera=(), microphone=(), geolocation=()" },
    { path = "/*", name = "Strict-Transport-Security", value = "max-age=63072000; includeSubDomains; preload" },
    { path = "/*", name = "Cache-Control", value = "public, max-age=0, must-revalidate" },
    { path = "/_astro/*", name = "Cache-Control", value = "public, max-age=31536000, immutable" },
  ]

  routes = [
    { source = "/relay/static/*", destination = "https://us-assets.i.posthog.com/static/*", type = "rewrite" },
    { source = "/relay/array/*", destination = "https://us-assets.i.posthog.com/array/*", type = "rewrite" },
    { source = "/relay/*", destination = "https://us.i.posthog.com/*", type = "rewrite" },
  ]
}

resource "render_env_group_link" "production_api" {
  env_group_id = render_env_group.production_api.id
  service_ids  = [render_web_service.production_api.id]
}

resource "render_env_group_link" "production_web" {
  env_group_id = render_env_group.production_web.id
  service_ids  = [render_web_service.production_web.id]
}
