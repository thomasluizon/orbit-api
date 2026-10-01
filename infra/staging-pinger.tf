resource "cloudflare_workers_script" "staging_pinger" {
  account_id         = "29945c90bc934c629c8e5a11cbfd146b"
  script_name        = "orbit-staging-pinger"
  main_module        = "staging-pinger.mjs"
  content            = file("${path.module}/workers/staging-pinger.mjs")
  compatibility_date = "2026-09-30"

  observability = {
    enabled            = true
    head_sampling_rate = 1
    logs = {
      enabled            = true
      invocation_logs    = true
      head_sampling_rate = 1
      persist            = true
    }
  }
}

resource "cloudflare_workers_cron_trigger" "staging_pinger" {
  account_id  = cloudflare_workers_script.staging_pinger.account_id
  script_name = cloudflare_workers_script.staging_pinger.script_name
  schedules = [
    { cron = "*/5 11-23 * * *" },
    { cron = "*/5 0-2 * * *" },
  ]
}
