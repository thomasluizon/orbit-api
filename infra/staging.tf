resource "render_web_service" "staging_api" {
  environment_id    = render_project.orbit.environments["Staging"].id
  name              = "orbit-api-staging"
  plan              = "free"
  region            = "ohio"
  health_check_path = "/health"
  custom_domains    = length(var.staging_api_custom_domains) > 0 ? [for domain in var.staging_api_custom_domains : { name = domain }] : null

  lifecycle {
    ignore_changes = [runtime_source.docker.branch]
  }

  runtime_source = {
    docker = {
      branch          = "redesign/main"
      repo_url        = "https://github.com/thomasluizon/orbit-api"
      dockerfile_path = "./Dockerfile"
      context         = "."
      auto_deploy     = false
    }
  }
}

resource "render_static_site" "staging_landing" {
  environment_id = render_project.orbit.environments["Staging"].id
  name           = "orbit-landing-staging"
  repo_url       = "https://github.com/thomasluizon/orbit-landing-page"
  branch         = "main"
  build_command  = "npm ci && npm run build"
  publish_path   = "dist"
  auto_deploy    = false

  lifecycle {
    ignore_changes = [branch]
  }

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

output "landing_staging_service_id" {
  description = "Render service ID for the landing staging release variable."
  value       = render_static_site.staging_landing.id
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
