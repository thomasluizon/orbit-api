resource "cloudflare_zone" "orbit" {
  account = {
    id = "29945c90bc934c629c8e5a11cbfd146b"
  }
  name = "useorbit.org"
  type = "full"
}

locals {
  existing_dns_records = {
    apex_cname = {
      name    = "@"
      type    = "CNAME"
      content = var.dns_apex_target
    }
    api_cname = {
      name    = "api.useorbit.org"
      type    = "CNAME"
      content = "orbit-api-8v9l.onrender.com"
    }
    app_cname = {
      name    = "app.useorbit.org"
      type    = "CNAME"
      content = var.dns_app_target
    }
    www_cname = {
      name    = "www.useorbit.org"
      type    = "CNAME"
      content = var.dns_www_target
    }
    apex_mx = {
      name     = "@"
      type     = "MX"
      content  = "smtp.google.com"
      priority = 1
    }
    send_send_mx = {
      name     = "send.send.useorbit.org"
      type     = "MX"
      content  = "feedback-smtp.sa-east-1.amazonses.com"
      priority = 10
    }
    send_updates_mx = {
      name     = "send.updates.useorbit.org"
      type     = "MX"
      content  = "feedback-smtp.sa-east-1.amazonses.com"
      priority = 10
    }
    dmarc_txt = {
      name    = "_dmarc.useorbit.org"
      type    = "TXT"
      content = "v=DMARC1; p=reject; rua=mailto:contact@useorbit.org"
    }
    google_verification_be2_txt = {
      name    = "@"
      type    = "TXT"
      content = "google-site-verification=BE2c4rFL5JQgZtBUdxiDdygZkCZTj8hfNmll5yCaAi0"
    }
    google_spf_txt = {
      name    = "@"
      type    = "TXT"
      content = "v=spf1 include:_spf.google.com ~all"
    }
    google_verification_4ia_txt = {
      name    = "@"
      type    = "TXT"
      content = "google-site-verification=4iacJ0j3y289KCVOwfHpXbgmteF2Zj5OVtCFMA8pTVE"
    }
    google_dkim_txt = {
      name = "google._domainkey.useorbit.org"
      type = "TXT"
      content = join("", [
        "v=DKIM1;k=rsa;p=MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAiQAGTCL07xJPn7B3AaF+dC+1ilvTJe/kW+FrOJAsFDHE1ezAWg6gS+ez68849wT1FCUmXAvUGmsi7t3tzhQCAYsc5kbPoAsP8IkqXKaZ78Fp+jCBThtgjojaze6WWZj4u494eghlRRvWTvwV4D9nIXoQAwo3HQhDJ5lIAVvN+hAGyJXPUBOeTt+k+MQVQdBHYqL",
        "36wl9YL5AJEc+HqjjsgtsPUdmDdPCF9+dglUWY1dqZU6kJ22mvkEz7tw6LHYytMkHO2tj6rktdNRzDz0haVSMuxAOAZcYt8rGSCnbeO6GyVuOoVAj7MGFxTZvpMJmTnVCnsntR7SMw6fVqD+WkQIDAQAB",
      ])
    }
    resend_send_dkim_txt = {
      name    = "resend._domainkey.send.useorbit.org"
      type    = "TXT"
      content = "p=MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQCgDK6PYGdlcmhMzg2vDtBnjRL7YC2VN8STsl6TdZSDO3W4onhBlLUcUh6OJQf4HkKU4sPVisqhs8MdiOCsU4rVzUTxPOn9Whp3l2L68f/i2cU7A5rDta+7L6DKlH7DAvHACt7c8RRc3SsZC5PfeX6L/3DaAV6TqqnVKLOySNL+DwIDAQAB"
    }
    resend_updates_dkim_txt = {
      name    = "resend._domainkey.updates.useorbit.org"
      type    = "TXT"
      content = "p=MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQC4pbAsyQBaFbgnyKGOJZBCWeyyHuc2Dfdg0Xn4AkMWicTfuyaoLOnvvIsU5whfFgw07+9FRkIurtIdxvWw9Pov48D27AlWJF9aJeufVfnLI1Uox4ugsSDmTJFagc3MSFB6wGoaqYzLHsefoa3/+vU12yfxGsw0fQ5n+yKltWcVywIDAQAB"
    }
    resend_send_spf_txt = {
      name    = "send.send.useorbit.org"
      type    = "TXT"
      content = "v=spf1 include:amazonses.com ~all"
    }
    resend_updates_spf_txt = {
      name    = "send.updates.useorbit.org"
      type    = "TXT"
      content = "v=spf1 include:amazonses.com ~all"
    }
  }

  staging_dns_records = {
    api_staging = {
      name    = "api-staging.useorbit.org"
      content = split("/", render_web_service.staging_api.url)[2]
    }
    staging = {
      name    = "staging.useorbit.org"
      content = split("/", render_web_service.staging_web.url)[2]
    }
  }
}

moved {
  from = cloudflare_dns_record.existing["apex_a"]
  to   = cloudflare_dns_record.existing["apex_cname"]
}

resource "cloudflare_dns_record" "existing" {
  for_each = local.existing_dns_records

  zone_id  = cloudflare_zone.orbit.id
  name     = each.value.name
  type     = each.value.type
  content  = each.value.content
  priority = try(each.value.priority, null)
  ttl      = 1
  proxied  = false
}

resource "cloudflare_dns_record" "staging" {
  for_each = local.staging_dns_records

  zone_id = cloudflare_zone.orbit.id
  name    = each.value.name
  type    = "CNAME"
  content = each.value.content
  ttl     = 1
  proxied = false
}

resource "cloudflare_turnstile_widget" "orbit" {
  account_id = "29945c90bc934c629c8e5a11cbfd146b"
  name       = "Orbit"
  mode       = "managed"
  domains = [
    "app.useorbit.org",
    "staging.useorbit.org",
    "useorbit.org",
    "www.useorbit.org",
  ]
}

resource "aws_ssm_parameter" "turnstile_secret" {
  for_each = toset(["production", "staging"])

  name  = "/orbit/${each.key}/api/BotProtection__SecretKey"
  type  = "SecureString"
  value = cloudflare_turnstile_widget.orbit.secret
}

output "cloudflare_name_servers" {
  description = "Assigned Cloudflare nameservers for the registrar switch."
  value       = cloudflare_zone.orbit.name_servers
}

output "turnstile_site_key" {
  description = "Public Turnstile site key for web and landing builds."
  value       = cloudflare_turnstile_widget.orbit.sitekey
}
