locals {
  api_secret_keys = toset([
    "AI__ApiKey",
    "Encryption__Key",
    "Firebase__CredentialsJson",
    "GooglePlay__ServiceAccountJson",
    "Google__ClientSecret",
    "Jwt__SecretKey",
    "Marketing__UnsubscribeSigningKey",
    "PostHog__ApiKey",
    "Resend__ApiKey",
    "SMOKE_TEST_CODE",
    "SMOKE_TEST_EMAIL",
    "Sentry__Dsn",
    "Stripe__SecretKey",
    "Stripe__WebhookSecret",
    "Supabase__AnonKey",
    "Supabase__SecretKey",
    "TEST_ACCOUNTS",
    "Vapid__PrivateKey",
    "Waitlist__SigningKey",
  ])

  staging_api_billing_keys = toset([
    "Stripe__MonthlyPriceIdBrl",
    "Stripe__MonthlyPriceIdUsd",
    "Stripe__ProProductId",
    "Stripe__YearlyPriceIdBrl",
    "Stripe__YearlyPriceIdUsd",
  ])

  production_api_values = {
    AI__BaseUrl                         = "https://api.openai.com/v1"
    AI__Model                           = "gpt-4.1-mini"
    ASPNETCORE_ENVIRONMENT              = "Production"
    Cors__AllowedOrigins__0             = "https://app.useorbit.org"
    Database__MigrateOnStartup          = "false"
    GooglePlay__MonthlyBasePlanId       = "monthly"
    GooglePlay__PackageName             = "org.useorbit.app"
    GooglePlay__ProductId               = "orbit_pro"
    GooglePlay__ReferralOfferId         = "referral10"
    GooglePlay__RtdnAudience            = "https://api.useorbit.org/api/subscriptions/play/rtdn"
    GooglePlay__RtdnServiceAccountEmail = "orbit-299@orbit-490614.iam.gserviceaccount.com"
    GooglePlay__YearlyBasePlanId        = "yearly"
    Google__AllowedRedirectUris__0      = "https://app.useorbit.org/auth-callback"
    Google__ClientId                    = "355604968359-tqco1o3l874mg7rvme91vniael7pn3tk.apps.googleusercontent.com"
    Jwt__Audience                       = "OrbitClient"
    Jwt__ExpiryHours                    = "168"
    Jwt__ExpiryMinutes                  = "0"
    Jwt__Issuer                         = "OrbitApi"
    Jwt__RefreshExpiryDays              = "90"
    Resend__FromEmail                   = "Orbit <noreply@send.useorbit.org>"
    Resend__SupportEmail                = "contact@useorbit.org"
    Sentry__Environment                 = "production"
    Stripe__CancelUrl                   = "https://app.useorbit.org/upgrade"
    Stripe__MonthlyPriceIdBrl           = "price_1U59khGwWZvarDk3duqWRGu7"
    Stripe__MonthlyPriceIdUsd           = "price_1U59miGwWZvarDk3c7Jomocl"
    Stripe__ProProductId                = "prod_UBUPrTlZg8chuk"
    Stripe__SuccessUrl                  = "https://app.useorbit.org/settings?subscription=success"
    Stripe__YearlyPriceIdBrl            = "price_1U59lVGwWZvarDk3FBO8ci6L"
    Stripe__YearlyPriceIdUsd            = "price_1U59ncGwWZvarDk3Ydiw7jP7"
    Supabase__Url                       = "https://wdscxamegetmhqldqsdg.supabase.co"
    Vapid__PublicKey                    = "BCotrosa_VZSere_khAKbxMVRj-NZIuHs4lK4sep1Fv5N6fx8z-99q9-pDPeEs0GwKiwOwf44SiI4NN5XX-htow"
    Vapid__Subject                      = "mailto:hello@useorbit.org"
  }

  staging_api_values = merge(local.production_api_values, {
    ASPNETCORE_ENVIRONMENT         = "Staging"
    Database__MigrateOnStartup     = "true"
    Cors__AllowedOrigins__0        = "https://staging.useorbit.org"
    Frontend__BaseUrl              = "https://staging.useorbit.org"
    GooglePlay__RtdnAudience       = "https://api-staging.useorbit.org/api/subscriptions/play/rtdn"
    Google__AllowedRedirectUris__0 = "https://staging.useorbit.org/auth-callback"
    Google__AllowedRedirectUris__1 = "https://app.useorbit.org/auth-callback"
    Marketing__ApiBaseUrl          = "https://api-staging.useorbit.org"
    Sentry__Environment            = "staging"
    Stripe__CancelUrl              = "https://staging.useorbit.org/upgrade"
    Stripe__SuccessUrl             = "https://staging.useorbit.org/settings?subscription=success"
    Supabase__Url                  = "https://staging-storage-disabled.invalid"
    Waitlist__ApiBaseUrl           = "https://api-staging.useorbit.org"
  })

  production_web_values = {
    API_BASE                   = "https://api.useorbit.org"
    NEXT_PUBLIC_EVENT_API_BASE = "https://api.useorbit.org"
    NEXT_PUBLIC_SITE_URL       = "https://app.useorbit.org"
    NODE_ENV                   = "production"
  }

  staging_web_values = {
    API_BASE                   = "https://api-staging.useorbit.org"
    NEXT_PUBLIC_EVENT_API_BASE = "https://api-staging.useorbit.org"
    NEXT_PUBLIC_SITE_URL       = "https://staging.useorbit.org"
    NODE_ENV                   = "production"
  }

  production_db_host = regex("@([^:/]+)", render_postgres.production.connection_info.internal_connection_string)[0]
  staging_db_host    = regex("@([^:/]+)", render_postgres.staging.connection_info.internal_connection_string)[0]
  production_db_name = regex("^[^:]+://[^/]+/([^/?]+)", render_postgres.production.connection_info.internal_connection_string)[0]
  staging_db_name    = regex("^[^:]+://[^/]+/([^/?]+)", render_postgres.staging.connection_info.internal_connection_string)[0]

  production_render_npgsql = "Host=${local.production_db_host};Port=5432;Database=${local.production_db_name};Username=${render_postgres.production.database_user};Password=\"${replace(render_postgres.production.connection_info.password, "\"", "\"\"")}\""
  staging_render_npgsql    = "Host=${local.staging_db_host};Port=5432;Database=${local.staging_db_name};Username=${render_postgres.staging.database_user};Password=\"${replace(render_postgres.staging.connection_info.password, "\"", "\"\"")}\""

  production_api_database_values = var.api_database == "supabase" ? {
    for key in local.database_keys : key => data.aws_ssm_parameter.production_api_database[key].value
    } : {
    for key in local.database_keys : key => local.production_render_npgsql
  }

  database_keys = toset([
    "ConnectionStrings__DefaultConnection",
    "ConnectionStrings__SessionConnection",
  ])
}

data "aws_ssm_parameter" "production_api" {
  for_each = local.api_secret_keys
  name     = "/orbit/production/api/${each.key}"
}

data "aws_ssm_parameter" "staging_api" {
  for_each = local.api_secret_keys
  name     = "/orbit/staging/api/${each.key}"
}

data "aws_ssm_parameter" "staging_api_billing" {
  for_each = local.staging_api_billing_keys
  name     = "/orbit/staging/api/${each.key}"
}

data "aws_ssm_parameter" "production_api_database" {
  for_each = var.api_database == "supabase" ? local.database_keys : toset([])
  name     = "/orbit/production/api/${each.key}"
}

data "aws_ssm_parameter" "production_web_sentry" {
  name = "/orbit/production/web/SENTRY_DSN"
}

data "aws_ssm_parameter" "staging_web_sentry" {
  name = "/orbit/staging/web/SENTRY_DSN"
}

resource "render_env_group" "production_api" {
  name = "orbit-production-api"
  env_vars = merge(
    { for key, value in local.production_api_values : key => { value = value } },
    { for key in local.api_secret_keys : key => { value = data.aws_ssm_parameter.production_api[key].value } },
    { for key, value in local.production_api_database_values : key => { value = value } },
    { BotProtection__SecretKey = { value = aws_ssm_parameter.turnstile_secret["production"].value } },
  )
}

resource "render_env_group" "staging_api" {
  name = "orbit-staging-api"
  env_vars = merge(
    { for key, value in local.staging_api_values : key => { value = value } },
    { for key in local.api_secret_keys : key => { value = data.aws_ssm_parameter.staging_api[key].value } },
    { for key in local.staging_api_billing_keys : key => { value = data.aws_ssm_parameter.staging_api_billing[key].value } },
    { for key in local.database_keys : key => { value = local.staging_render_npgsql } },
    { BotProtection__SecretKey = { value = aws_ssm_parameter.turnstile_secret["staging"].value } },
  )
}

resource "render_env_group" "production_web" {
  name = "orbit-production-web"
  env_vars = merge(
    { for key, value in local.production_web_values : key => { value = value } },
    { SENTRY_DSN = { value = data.aws_ssm_parameter.production_web_sentry.value } },
  )
}

resource "render_env_group" "staging_web" {
  name = "orbit-staging-web"
  env_vars = merge(
    { for key, value in local.staging_web_values : key => { value = value } },
    { SENTRY_DSN = { value = data.aws_ssm_parameter.staging_web_sentry.value } },
  )
}
