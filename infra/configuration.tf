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

  production_api_values = {
    AI__BaseUrl                         = "https://api.openai.com/v1"
    AI__Model                           = "gpt-4.1-mini"
    ASPNETCORE_ENVIRONMENT              = "Production"
    Cors__AllowedOrigins__0             = "https://app.useorbit.org"
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
    Stripe__PublishableKey              = "pk_live_51TD75tGwWZvarDk3YaoxByBsqgWLkjVZgoJptemlASbjn4HzU1TGTgwVm3teV3kOi6wyq7efqnAUeUXsjvNYN5IT00hnfdEHFJ"
    Stripe__SuccessUrl                  = "https://app.useorbit.org/settings?subscription=success"
    Stripe__YearlyPriceIdBrl            = "price_1U59lVGwWZvarDk3FBO8ci6L"
    Stripe__YearlyPriceIdUsd            = "price_1U59ncGwWZvarDk3Ydiw7jP7"
    Supabase__Url                       = "https://wdscxamegetmhqldqsdg.supabase.co"
    Storage__Provider                   = var.production_storage_provider
    Storage__S3__Bucket                 = aws_s3_bucket.uploads["production"].bucket
    Storage__S3__Region                 = "us-east-2"
    Vapid__PublicKey                    = "BCotrosa_VZSere_khAKbxMVRj-NZIuHs4lK4sep1Fv5N6fx8z-99q9-pDPeEs0GwKiwOwf44SiI4NN5XX-htow"
    Vapid__Subject                      = "mailto:hello@useorbit.org"
  }

  staging_api_values = merge(local.production_api_values, {
    ASPNETCORE_ENVIRONMENT         = "Staging"
    Cors__AllowedOrigins__0        = "https://staging.useorbit.org"
    Frontend__BaseUrl              = "https://staging.useorbit.org"
    Google__AllowedRedirectUris__0 = "https://staging.useorbit.org/auth-callback"
    Google__AllowedRedirectUris__1 = "https://app.useorbit.org/auth-callback"
    Marketing__ApiBaseUrl          = "https://api-staging.useorbit.org"
    Sentry__Environment            = "staging"
    Stripe__CancelUrl              = "https://staging.useorbit.org/upgrade"
    Stripe__MonthlyPriceIdBrl      = "price_staging_unset_monthly_brl"
    Stripe__MonthlyPriceIdUsd      = "price_staging_unset_monthly_usd"
    Stripe__ProProductId           = "prod_staging_unset"
    Stripe__PublishableKey         = "pk_test_staging_unset"
    Stripe__SuccessUrl             = "https://staging.useorbit.org/settings?subscription=success"
    Stripe__YearlyPriceIdBrl       = "price_staging_unset_yearly_brl"
    Stripe__YearlyPriceIdUsd       = "price_staging_unset_yearly_usd"
    Supabase__Url                  = "https://staging-storage-disabled.invalid"
    Storage__Provider              = var.staging_storage_provider
    Storage__S3__Bucket            = aws_s3_bucket.uploads["staging"].bucket
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

  production_render_npgsql = "Host=${local.production_db_host};Port=5432;Database=orbit_production;Username=orbit_production;Password=\"${replace(render_postgres.production.connection_info.password, "\"", "\"\"")}\""
  staging_render_npgsql    = "Host=${local.staging_db_host};Port=5432;Database=orbit_staging;Username=orbit_staging;Password=\"${replace(render_postgres.staging.connection_info.password, "\"", "\"\"")}\""

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
    {
      Storage__S3__AccessKeyId     = { value = aws_ssm_parameter.uploads_access_key_id["production"].value }
      Storage__S3__SecretAccessKey = { value = aws_ssm_parameter.uploads_secret_access_key["production"].value }
    },
  )
}

resource "render_env_group" "staging_api" {
  name = "orbit-staging-api"
  env_vars = merge(
    { for key, value in local.staging_api_values : key => { value = value } },
    { for key in local.api_secret_keys : key => { value = data.aws_ssm_parameter.staging_api[key].value } },
    { for key in local.database_keys : key => { value = local.staging_render_npgsql } },
    {
      Storage__S3__AccessKeyId     = { value = aws_ssm_parameter.uploads_access_key_id["staging"].value }
      Storage__S3__SecretAccessKey = { value = aws_ssm_parameter.uploads_secret_access_key["staging"].value }
    },
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
