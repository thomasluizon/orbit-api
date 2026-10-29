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
    "SMOKE_TEST_CODE",
    "SMOKE_TEST_EMAIL",
    "Sentry__Dsn",
    "Stripe__SecretKey",
    "Stripe__WebhookSecret",
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
    AI__Model                           = "gpt-6-luna"
    AI__SubTaskModel                    = "gpt-6-luna"
    ASPNETCORE_ENVIRONMENT              = "Production"
    Cors__AllowedOrigins__0             = "https://app.useorbit.org"
    Database__MigrateOnStartup          = "false"
    DesktopAuth__RedirectUri            = "org.useorbit.desktop://auth/callback"
    GooglePlay__MonthlyBasePlanId       = "monthly"
    GooglePlay__PackageName             = "org.useorbit.app"
    GooglePlay__ProductId               = "orbit_pro"
    GooglePlay__ReferralOfferId         = "referral10"
    GooglePlay__RtdnAudience            = "https://api.useorbit.org/api/subscriptions/play/rtdn"
    GooglePlay__RtdnServiceAccountEmail = "orbit-299@orbit-490614.iam.gserviceaccount.com"
    GooglePlay__YearlyBasePlanId        = "yearly"
    Google__AllowedRedirectUris__0      = "https://app.useorbit.org/auth-callback"
    Google__AllowedRedirectUris__1      = "https://api.useorbit.org/oauth/google/callback"
    Google__ClientId                    = "355604968359-tqco1o3l874mg7rvme91vniael7pn3tk.apps.googleusercontent.com"
    Jwt__Audience                       = "OrbitClient"
    Jwt__ExpiryHours                    = "0"
    Jwt__ExpiryMinutes                  = "15"
    Jwt__Issuer                         = "OrbitApi"
    Jwt__RefreshExpiryDays              = "90"
    Ses__Region                         = "us-east-2"
    Ses__FromEmail                      = "Orbit <contact@useorbit.org>"
    Ses__MarketingFromEmail             = "Orbit <news@updates.useorbit.org>"
    Ses__SupportEmail                   = "contact@useorbit.org"
    Ses__TransactionalConfigurationSet  = aws_sesv2_configuration_set.orbit["transactional"].configuration_set_name
    Ses__MarketingConfigurationSet      = aws_sesv2_configuration_set.orbit["marketing"].configuration_set_name
    Ses__TopicArn                       = aws_sns_topic.ses_events.arn
    Sentry__Environment                 = "production"
    Stripe__CancelUrl                   = "https://app.useorbit.org/upgrade"
    Stripe__MonthlyPriceIdBrl           = "price_1U59khGwWZvarDk3duqWRGu7"
    Stripe__MonthlyPriceIdUsd           = "price_1U59miGwWZvarDk3c7Jomocl"
    Stripe__ProProductId                = "prod_UBUPrTlZg8chuk"
    Stripe__SuccessUrl                  = "https://app.useorbit.org/upgrade?subscription=success"
    Stripe__YearlyPriceIdBrl            = "price_1U59lVGwWZvarDk3FBO8ci6L"
    Stripe__YearlyPriceIdUsd            = "price_1U59ncGwWZvarDk3Ydiw7jP7"
    Storage__S3__Bucket                 = aws_s3_bucket.uploads["production"].bucket
    Storage__S3__Region                 = "us-east-2"
    Storage__S3__PublicBaseUrl          = "https://api.useorbit.org"
    Vapid__PublicKey                    = "BCotrosa_VZSere_khAKbxMVRj-NZIuHs4lK4sep1Fv5N6fx8z-99q9-pDPeEs0GwKiwOwf44SiI4NN5XX-htow"
    Vapid__Subject                      = "mailto:hello@useorbit.org"
  }

  staging_api_values = merge(local.production_api_values, {
    Ses__TransactionalConfigurationSet = aws_sesv2_configuration_set.staging["transactional"].configuration_set_name
    Ses__MarketingConfigurationSet     = aws_sesv2_configuration_set.staging["marketing"].configuration_set_name
    Ses__TopicArn                      = aws_sns_topic.ses_events_staging.arn
    ASPNETCORE_ENVIRONMENT             = "Staging"
    Database__MigrateOnStartup         = "true"
    DesktopAuth__RedirectUri           = "org.useorbit.desktop.staging://auth/callback"
    Cors__AllowedOrigins__0            = "https://app-staging.useorbit.org"
    Cors__AllowedOrigins__1            = "https://staging.useorbit.org"
    Cors__LandingOrigins__2            = "https://orbit-landing-staging.onrender.com"
    Frontend__BaseUrl                  = "https://app-staging.useorbit.org"
    GooglePlay__PackageName            = "org.useorbit.app.staging"
    GooglePlay__RtdnAudience           = "https://api-staging.useorbit.org/api/subscriptions/play/rtdn"
    Google__AllowedRedirectUris__0     = "https://app-staging.useorbit.org/auth-callback"
    Google__AllowedRedirectUris__1     = "https://app.useorbit.org/auth-callback"
    Google__AllowedRedirectUris__2     = "https://staging.useorbit.org/auth-callback"
    Google__AllowedRedirectUris__3     = "https://api-staging.useorbit.org/oauth/google/callback"
    Marketing__ApiBaseUrl              = "https://api-staging.useorbit.org"
    Sentry__Environment                = "staging"
    Stripe__CancelUrl                  = "https://app-staging.useorbit.org/upgrade"
    Stripe__SuccessUrl                 = "https://app-staging.useorbit.org/upgrade?subscription=success"
    Storage__S3__Bucket                = aws_s3_bucket.uploads["staging"].bucket
    Storage__S3__PublicBaseUrl         = "https://api-staging.useorbit.org"
    Waitlist__ApiBaseUrl               = "https://api-staging.useorbit.org"
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
    NEXT_PUBLIC_SITE_URL       = "https://app-staging.useorbit.org"
    NODE_ENV                   = "production"
  }

  production_db_host = regex("@([^:/]+)", render_postgres.production.connection_info.internal_connection_string)[0]
  production_db_name = regex("^[^:]+://[^/]+/([^/?]+)", render_postgres.production.connection_info.internal_connection_string)[0]

  production_render_npgsql = "Host=${local.production_db_host};Port=5432;Database=${local.production_db_name};Username=${render_postgres.production.database_user};Password=\"${replace(render_postgres.production.connection_info.password, "\"", "\"\"")}\""

  production_api_database_values = {
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

data "aws_ssm_parameter" "staging_api_eval_account_ids" {
  name = "/orbit/staging/api/StagingEval__AccountIds"
}

data "aws_ssm_parameter" "production_web_sentry" {
  name = "/orbit/production/web/SENTRY_DSN"
}

data "aws_ssm_parameter" "staging_web_sentry" {
  name = "/orbit/staging/web/SENTRY_DSN"
}

resource "random_password" "bff_forwarding_secret" {
  for_each = toset(["production", "staging"])

  length  = 64
  special = false
}

resource "aws_ssm_parameter" "bff_forwarding_secret" {
  for_each = random_password.bff_forwarding_secret

  name  = "/orbit/${each.key}/api/Auth__BffForwardingSecret"
  type  = "SecureString"
  value = each.value.result
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
    { BotProtection__SecretKey = { value = aws_ssm_parameter.turnstile_secret["production"].value } },
    { for key, value in local.onboarding_api_values["production"] : key => { value = value } },
    { OnboardingSecurity__SigningSecret = { value = aws_ssm_parameter.onboarding_signing_secret_production.value } },
    { Auth__BffForwardingSecret = { value = aws_ssm_parameter.bff_forwarding_secret["production"].value } },
    { Ses__AccessKeyId = { value = aws_ssm_parameter.api_ses_access_key_id["production"].value } },
    { Ses__SecretAccessKey = { value = aws_ssm_parameter.api_ses_secret_access_key["production"].value } },
  )
}

resource "render_env_group" "staging_api" {
  name = "orbit-staging-api"
  env_vars = merge(
    { for key, value in local.staging_api_values : key => { value = value } },
    { for key in local.api_secret_keys : key => { value = data.aws_ssm_parameter.staging_api[key].value } },
    { for key in local.staging_api_billing_keys : key => { value = data.aws_ssm_parameter.staging_api_billing[key].value } },
    { StagingEval__AccountIds = { value = data.aws_ssm_parameter.staging_api_eval_account_ids.value } },
    {
      Storage__S3__AccessKeyId     = { value = aws_ssm_parameter.uploads_access_key_id["staging"].value }
      Storage__S3__SecretAccessKey = { value = aws_ssm_parameter.uploads_secret_access_key["staging"].value }
    },
    { BotProtection__SecretKey = { value = aws_ssm_parameter.turnstile_secret["staging"].value } },
    { for key, value in local.onboarding_api_values["staging"] : key => { value = value } },
    { OnboardingSecurity__SigningSecret = { value = aws_ssm_parameter.onboarding_signing_secret_staging.value } },
    { Auth__BffForwardingSecret = { value = aws_ssm_parameter.bff_forwarding_secret["staging"].value } },
    { Ses__AccessKeyId = { value = aws_ssm_parameter.api_ses_access_key_id["staging"].value } },
    { Ses__SecretAccessKey = { value = aws_ssm_parameter.api_ses_secret_access_key["staging"].value } },
  )
}

resource "render_env_group" "production_web" {
  name = "orbit-production-web"
  env_vars = merge(
    { for key, value in local.production_web_values : key => { value = value } },
    { SENTRY_DSN = { value = data.aws_ssm_parameter.production_web_sentry.value } },
    { BFF_FORWARDING_SECRET = { value = aws_ssm_parameter.bff_forwarding_secret["production"].value } },
  )
}

resource "render_env_group" "staging_web" {
  name = "orbit-staging-web"
  env_vars = merge(
    { for key, value in local.staging_web_values : key => { value = value } },
    { SENTRY_DSN = { value = data.aws_ssm_parameter.staging_web_sentry.value } },
    { BFF_FORWARDING_SECRET = { value = aws_ssm_parameter.bff_forwarding_secret["staging"].value } },
  )
}
