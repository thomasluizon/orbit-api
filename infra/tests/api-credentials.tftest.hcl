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
  production_api_digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  staging_api_digest    = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  production_web_digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  staging_web_digest    = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
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
