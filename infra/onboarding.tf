locals {
  onboarding_allowed_origins = {
    production = [
      "https://app.useorbit.org",
    ]
    staging = [
      "https://app-staging.useorbit.org",
    ]
  }

  bot_protection_landing_hostnames = {
    production = ["useorbit.org", "www.useorbit.org"]
    staging    = []
  }

  bot_protection_api_values = {
    for environment, origins in local.onboarding_allowed_origins : environment => {
      for index, hostname in sort(setintersection(
        toset(concat([for origin in origins : trimprefix(origin, "https://")], local.bot_protection_landing_hostnames[environment])),
        toset(cloudflare_turnstile_widget.orbit.domains)
      )) : "BotProtection__AllowedHostnames__${index}" => hostname
    }
  }

  onboarding_platform_values = {
    for environment, parameters in data.aws_ssm_parameters_by_path.onboarding_platform : environment => {
      for name, value in zipmap(parameters.names, parameters.values) :
      trimprefix(name, "/orbit/${environment}/api/") => value
      if can(regex("^/orbit/${environment}/api/OnboardingSecurity__(PlayIntegrityCredentialsJson|AndroidPackageName|AndroidCertificateDigests__[0-9]+|AppleTeamId|AppleBundleId|AppleBundleVersions__[0-9]+|AppleValidationCategories__[0-9]+)$", name))
    }
  }

  onboarding_api_values = {
    for environment, origins in local.onboarding_allowed_origins : environment => merge(
      { for index, origin in origins : "OnboardingSecurity__AllowedOrigins__${index}" => origin },
      local.onboarding_platform_values[environment],
    )
  }
}

data "aws_ssm_parameters_by_path" "onboarding_platform" {
  for_each = toset(["production", "staging"])

  path            = "/orbit/${each.key}/api"
  recursive       = false
  with_decryption = true
}

resource "random_password" "onboarding_signing_secret_production" {
  length  = 64
  special = false
}

resource "random_password" "onboarding_signing_secret_staging" {
  length  = 64
  special = false
}

resource "aws_ssm_parameter" "onboarding_signing_secret_production" {
  name  = "/orbit/production/api/OnboardingSecurity__SigningSecret"
  type  = "SecureString"
  value = random_password.onboarding_signing_secret_production.result
}

resource "aws_ssm_parameter" "onboarding_signing_secret_staging" {
  name  = "/orbit/staging/api/OnboardingSecurity__SigningSecret"
  type  = "SecureString"
  value = random_password.onboarding_signing_secret_staging.result
}
