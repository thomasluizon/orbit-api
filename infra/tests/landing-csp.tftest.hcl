mock_provider "aws" { override_during = plan }
mock_provider "render" { override_during = plan }
mock_provider "cloudflare" { override_during = plan }
mock_provider "random" { override_during = plan }
override_resource { target = render_project.orbit }
variables {
  production_api_digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  staging_api_digest    = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  production_web_digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  staging_web_digest    = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
}
run "landing_enforces_both_environments" {
  command = plan
  plan_options {
    target = [render_static_site.landing, render_static_site.staging_landing]
  }
  assert {
    condition = alltrue([
      one([for h in render_static_site.landing.headers : h.value if h.name == "Content-Security-Policy"]) == local.landing_csp.production,
      one([for h in render_static_site.staging_landing.headers : h.value if h.name == "Content-Security-Policy"]) == local.landing_csp.staging,
      strcontains(local.landing_csp.production, "connect-src 'self' https://api.useorbit.org https://challenges.cloudflare.com"),
      strcontains(local.landing_csp.staging, "connect-src 'self' https://api-staging.useorbit.org https://challenges.cloudflare.com"),
      strcontains(local.landing_csp.production, "script-src 'self' https://challenges.cloudflare.com"),
      strcontains(local.landing_csp.production, "frame-src https://challenges.cloudflare.com"),
      strcontains(local.landing_csp.production, "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'"),
      !strcontains(local.landing_csp.production, "unsafe-inline"),
    ])
    error_message = "Landing CSP must enforce the environment API, Turnstile and exact inline hashes without unsafe-inline."
  }
}
run "staging_report_only_precedes_enforcement" {
  command = plan
  variables {
    staging_landing_csp_report_only = true
  }
  plan_options {
    target = [render_static_site.landing, render_static_site.staging_landing]
  }
  assert {
    condition = alltrue([
      one([for h in render_static_site.staging_landing.headers : h.value if h.name == "Content-Security-Policy-Report-Only"]) == local.landing_csp.staging,
      one([for h in render_static_site.landing.headers : h.value if h.name == "Content-Security-Policy"]) == local.landing_csp.production,
    ])
    error_message = "Report-only rollout changes only the staging header name."
  }
}
