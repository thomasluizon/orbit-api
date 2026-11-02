mock_provider "aws" {
  override_during = plan

  mock_data "aws_ssm_parameter" {
    defaults = {
      value = "test-secret"
    }
  }
}

mock_provider "render" {
  override_during = plan

  mock_resource "render_postgres" {
    defaults = {
      connection_info = {
        internal_connection_string = "postgresql://orbit:password@postgres.local/orbit"
        password                   = "password"
      }
    }
  }
}

mock_provider "random" {
  override_during = plan
}

mock_provider "cloudflare" {
  override_during = plan
}

override_resource {
  target          = render_web_service.production_api
  override_during = plan
}

override_resource {
  target = render_project.orbit
}

override_resource {
  target          = render_web_service.production_web
  override_during = plan
}
override_resource {
  target          = render_env_group.production_api
  override_during = plan
  values          = { id = "env-production-api" }
}
override_resource {
  target          = render_env_group.production_web
  override_during = plan
  values          = { id = "env-production-web" }
}

variables {
  production_api_digest             = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  staging_api_digest                = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  production_web_digest             = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  staging_web_digest                = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  production_postgres_ip_allow_list = [{ cidr_block = "192.0.2.1/32", description = "Test operator" }]
  web_custom_domains                = ["app.useorbit.org"]
}

run "production_values" {
  command = plan
  plan_options {
    target = [render_web_service.production_api, render_web_service.production_web, render_env_group.production_api, render_env_group.production_web, render_env_group_link.production_api, render_env_group_link.production_web]
  }
  assert {
    condition = alltrue([
      render_env_group.production_api.env_vars["ASPNETCORE_ENVIRONMENT"].value == "Production",
      render_env_group.production_api.env_vars["Cors__AllowedOrigins__0"].value == "https://app.useorbit.org",
      render_env_group.production_api.env_vars["Database__MigrateOnStartup"].value == "true",
      render_env_group.production_api.env_vars["Google__AllowedRedirectUris__0"].value == "https://app.useorbit.org/auth-callback",
      render_env_group.production_api.env_vars["Google__AllowedRedirectUris__1"].value == "https://api.useorbit.org/oauth/google/callback",
      render_env_group.production_api.env_vars["Google__ClientId"].value == "355604968359-tqco1o3l874mg7rvme91vniael7pn3tk.apps.googleusercontent.com",
      render_env_group.production_api.env_vars["Jwt__Issuer"].value == "OrbitApi",
      render_env_group.production_api.env_vars["Jwt__Audience"].value == "OrbitClient",
      render_env_group.production_api.env_vars["Ses__Region"].value == "us-east-2",
      render_env_group.production_api.env_vars["Ses__FromEmail"].value == "Orbit <contact@useorbit.org>",
      render_env_group.production_api.env_vars["Sentry__Environment"].value == "production",
      render_env_group.production_api.env_vars["Storage__S3__Bucket"].value == "orbit-uploads-production-713285551626",
      render_env_group.production_api.env_vars["Storage__S3__PublicBaseUrl"].value == "https://api.useorbit.org",
      render_env_group.production_web.env_vars["API_BASE"].value == "https://api.useorbit.org",
      render_env_group.production_web.env_vars["NEXT_PUBLIC_SITE_URL"].value == "https://app.useorbit.org",
      render_env_group.production_web.env_vars["NODE_ENV"].value == "production",
      render_web_service.production_api.env_vars["ORBIT_TERRAFORM_ENV_GROUP"].value == "production-api",
      render_web_service.production_web.env_vars["ORBIT_TERRAFORM_ENV_GROUP"].value == "production-web",
      render_env_group_link.production_api.env_group_id == render_env_group.production_api.id,
      contains(render_env_group_link.production_api.service_ids, render_web_service.production_api.id),
      render_env_group_link.production_web.env_group_id == render_env_group.production_web.id,
      contains(render_env_group_link.production_web.service_ids, render_web_service.production_web.id),
      render_web_service.production_api.max_shutdown_delay_seconds == 300,
      render_web_service.production_web.max_shutdown_delay_seconds == 300,
      render_postgres.production.plan == "basic_1gb",
      render_postgres.production.version == "17",
      render_postgres.production.high_availability_enabled == false,
      length(render_postgres.production.ip_allow_list) > 0,
    ])
    error_message = "Production auth, CORS, migration, sender, storage, web, links, shutdown and database settings must match the reviewed values."
  }
}

run "empty_database_allow_list_rejected" {
  command = plan
  variables {
    production_postgres_ip_allow_list = []
  }
  plan_options {
    target = [render_postgres.production]
  }
  expect_failures = [render_postgres.production]
}
