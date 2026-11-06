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
  target = render_web_service.production_api
}

override_resource {
  target = render_project.orbit
}

variables {
  production_postgres_ip_allow_list = [{ cidr_block = "192.0.2.1/32", description = "Test operator" }]
  production_api_digest             = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  staging_api_digest                = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  production_web_digest             = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  staging_web_digest                = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
}

run "evaluation_keys_absent" {
  command = plan

  plan_options {
    target = [render_env_group.production_api, render_env_group.staging_api]
  }

  assert {
    condition = alltrue([
      !contains(keys(data.aws_ssm_parameter.production_api), "AI__EvalApiKey"),
      !contains(keys(data.aws_ssm_parameter.staging_api), "AI__EvalApiKey"),
      !contains(keys(render_env_group.production_api.env_vars), "AI__EvalApiKey"),
      !contains(keys(render_env_group.staging_api.env_vars), "AI__EvalApiKey"),
    ])
    error_message = "Missing evaluation keys must neither be requested nor included in either API group."
  }
}

run "evaluation_keys_present" {
  command = plan

  plan_options {
    target = [render_env_group.production_api, render_env_group.staging_api]
  }

  override_data {
    target = data.aws_ssm_parameters_by_path.onboarding_platform["production"]
    values = {
      names  = ["/orbit/production/api/AI__EvalApiKey", "/orbit/production/api/UnrelatedKey"]
      values = ["unused-encrypted-eval-key", "unused-unrelated-value"]
    }
  }

  override_data {
    target = data.aws_ssm_parameters_by_path.onboarding_platform["staging"]
    values = {
      names  = ["/orbit/staging/api/AI__EvalApiKey", "/orbit/staging/api/UnrelatedKey"]
      values = ["unused-encrypted-eval-key", "unused-unrelated-value"]
    }
  }

  override_data {
    target = data.aws_ssm_parameter.production_api["AI__EvalApiKey"]
    values = { value = "production-eval-test-key" }
  }

  override_data {
    target = data.aws_ssm_parameter.staging_api["AI__EvalApiKey"]
    values = { value = "staging-eval-test-key" }
  }

  assert {
    condition = alltrue([
      render_env_group.production_api.env_vars["AI__EvalApiKey"].value == "production-eval-test-key",
      render_env_group.staging_api.env_vars["AI__EvalApiKey"].value == "staging-eval-test-key",
      !contains(keys(render_env_group.production_api.env_vars), "UnrelatedKey"),
      !contains(keys(render_env_group.staging_api.env_vars), "UnrelatedKey"),
    ])
    error_message = "Existing evaluation keys must reach only their own API group, without importing unrelated parameters."
  }
}

run "only_production_evaluation_key_present" {
  command = plan

  plan_options {
    target = [render_env_group.production_api, render_env_group.staging_api]
  }

  override_data {
    target = data.aws_ssm_parameters_by_path.onboarding_platform["production"]
    values = {
      names  = ["/orbit/production/api/AI__EvalApiKey"]
      values = ["unused-path-eval-key"]
    }
  }

  override_data {
    target = data.aws_ssm_parameters_by_path.onboarding_platform["staging"]
    values = {
      names  = ["/orbit/production/api/AI__EvalApiKey"]
      values = ["unused-wrong-environment-key"]
    }
  }

  assert {
    condition = alltrue([
      render_env_group.production_api.env_vars["AI__EvalApiKey"].value == "test-secret",
      !contains(keys(data.aws_ssm_parameter.staging_api), "AI__EvalApiKey"),
      !contains(keys(render_env_group.staging_api.env_vars), "AI__EvalApiKey"),
    ])
    error_message = "A production evaluation key must not require or populate a staging key."
  }
}

run "only_staging_evaluation_key_present" {
  command = plan

  plan_options {
    target = [render_env_group.production_api, render_env_group.staging_api]
  }

  override_data {
    target = data.aws_ssm_parameters_by_path.onboarding_platform["staging"]
    values = {
      names  = ["/orbit/staging/api/AI__EvalApiKey"]
      values = ["unused-path-eval-key"]
    }
  }

  override_data {
    target = data.aws_ssm_parameters_by_path.onboarding_platform["production"]
    values = {
      names  = ["/orbit/staging/api/AI__EvalApiKey"]
      values = ["unused-wrong-environment-key"]
    }
  }

  assert {
    condition = alltrue([
      render_env_group.staging_api.env_vars["AI__EvalApiKey"].value == "test-secret",
      !contains(keys(data.aws_ssm_parameter.production_api), "AI__EvalApiKey"),
      !contains(keys(render_env_group.production_api.env_vars), "AI__EvalApiKey"),
    ])
    error_message = "A staging evaluation key must not require or populate a production key."
  }
}

run "wns_credentials_without_evaluation_keys" {
  command = plan

  plan_options {
    target = [render_env_group.production_api, render_env_group.staging_api]
  }

  override_data {
    target = data.aws_ssm_parameter.production_api["Wns__ClientSecret"]
    values = { value = "production-wns-test-secret" }
  }

  override_data {
    target = data.aws_ssm_parameter.staging_api["Wns__ClientSecret"]
    values = { value = "staging-wns-test-secret" }
  }

  assert {
    condition = alltrue([
      render_env_group.production_api.env_vars["Wns__TenantId"].value == "d0a72506-23da-4004-afff-9c077a5b72a5",
      render_env_group.staging_api.env_vars["Wns__TenantId"].value == "d0a72506-23da-4004-afff-9c077a5b72a5",
      render_env_group.production_api.env_vars["Wns__ClientId"].value == "d8d3802e-fed9-4d7e-a7b9-87fdfcf2d0db",
      render_env_group.staging_api.env_vars["Wns__ClientId"].value == "d8d3802e-fed9-4d7e-a7b9-87fdfcf2d0db",
      render_env_group.production_api.env_vars["Wns__ClientSecret"].value == "production-wns-test-secret",
      render_env_group.staging_api.env_vars["Wns__ClientSecret"].value == "staging-wns-test-secret",
      data.aws_ssm_parameter.production_api["Wns__ClientSecret"].name == "/orbit/production/api/Wns__ClientSecret",
      data.aws_ssm_parameter.staging_api["Wns__ClientSecret"].name == "/orbit/staging/api/Wns__ClientSecret",
      !contains(keys(render_env_group.production_api.env_vars), "AI__EvalApiKey"),
      !contains(keys(render_env_group.staging_api.env_vars), "AI__EvalApiKey"),
    ])
    error_message = "Both API groups need the public WNS identifiers and their own required SSM secret even without evaluation keys."
  }
}

run "apple_credentials" {
  command = plan

  plan_options {
    target = [render_env_group.production_api, render_env_group.staging_api]
  }

  override_data {
    target = data.aws_ssm_parameter.production_api["AppStore__PrivateKey"]
    values = { value = "production-app-store-test-key" }
  }

  override_data {
    target = data.aws_ssm_parameter.staging_api["AppStore__PrivateKey"]
    values = { value = "staging-app-store-test-key" }
  }

  override_data {
    target = data.aws_ssm_parameter.production_api["AppleSignIn__PrivateKey"]
    values = { value = "production-apple-sign-in-test-key" }
  }

  override_data {
    target = data.aws_ssm_parameter.staging_api["AppleSignIn__PrivateKey"]
    values = { value = "staging-apple-sign-in-test-key" }
  }

  override_data {
    target = data.aws_ssm_parameter.production_api["Apns__PrivateKey"]
    values = { value = "production-apns-test-key" }
  }

  override_data {
    target = data.aws_ssm_parameter.staging_api["Apns__PrivateKey"]
    values = { value = "staging-apns-test-key" }
  }

  assert {
    condition = alltrue([
      render_env_group.production_api.env_vars["AppStore__PrivateKey"].value == "production-app-store-test-key",
      render_env_group.staging_api.env_vars["AppStore__PrivateKey"].value == "staging-app-store-test-key",
      render_env_group.production_api.env_vars["AppleSignIn__PrivateKey"].value == "production-apple-sign-in-test-key",
      render_env_group.staging_api.env_vars["AppleSignIn__PrivateKey"].value == "staging-apple-sign-in-test-key",
      render_env_group.production_api.env_vars["Apns__PrivateKey"].value == "production-apns-test-key",
      render_env_group.staging_api.env_vars["Apns__PrivateKey"].value == "staging-apns-test-key",
      data.aws_ssm_parameter.production_api["AppStore__PrivateKey"].name == "/orbit/production/api/AppStore__PrivateKey",
      data.aws_ssm_parameter.staging_api["AppStore__PrivateKey"].name == "/orbit/staging/api/AppStore__PrivateKey",
      data.aws_ssm_parameter.production_api["AppleSignIn__PrivateKey"].name == "/orbit/production/api/AppleSignIn__PrivateKey",
      data.aws_ssm_parameter.staging_api["AppleSignIn__PrivateKey"].name == "/orbit/staging/api/AppleSignIn__PrivateKey",
      data.aws_ssm_parameter.production_api["Apns__PrivateKey"].name == "/orbit/production/api/Apns__PrivateKey",
      data.aws_ssm_parameter.staging_api["Apns__PrivateKey"].name == "/orbit/staging/api/Apns__PrivateKey",
    ])
    error_message = "Both API groups need their own Apple signing keys from SSM."
  }

  assert {
    condition = alltrue([
      render_env_group.production_api.env_vars["AppStore__BundleId"].value == "org.useorbit.app",
      render_env_group.staging_api.env_vars["AppStore__BundleId"].value == "org.useorbit.app.staging",
      render_env_group.production_api.env_vars["AppStore__AppAppleId"].value == "6831372835",
      render_env_group.staging_api.env_vars["AppStore__AppAppleId"].value == "6831373752",
      render_env_group.production_api.env_vars["AppleSignIn__WebOrigin"].value == "https://app.useorbit.org",
      render_env_group.staging_api.env_vars["AppleSignIn__WebOrigin"].value == "https://app-staging.useorbit.org",
      render_env_group.production_api.env_vars["Apns__Topic"].value == "org.useorbit.app",
      render_env_group.staging_api.env_vars["Apns__Topic"].value == "org.useorbit.app.staging",
      render_env_group.production_api.env_vars["Apns__UseSandbox"].value == "false",
      render_env_group.staging_api.env_vars["Apns__UseSandbox"].value == "false",
    ])
    error_message = "Each API group needs the Apple identifiers of its own app."
  }
}
