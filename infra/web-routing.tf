resource "cloudflare_workers_script" "web_routing" {
  account_id         = cloudflare_zone.orbit.account.id
  script_name        = "orbit-web-routing"
  main_module        = "web-routing.mjs"
  content            = file("${path.module}/workers/web-routing.mjs")
  compatibility_date = "2026-07-30"

  bindings = [{
    name = "REDESIGN_HOST"
    type = "plain_text"
    text = split("/", render_web_service.production_redesign_web.url)[2]
  }]
}

resource "cloudflare_workers_route" "web_routing" {
  zone_id = cloudflare_zone.orbit.id
  pattern = "app.useorbit.org/*"
  script  = cloudflare_workers_script.web_routing.script_name

  depends_on = [cloudflare_dns_record.existing, render_env_group_link.production_web]
}
