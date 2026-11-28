import {
  to = cloudflare_workers_script.staging_assetlinks
  id = "29945c90bc934c629c8e5a11cbfd146b/orbit-staging-assetlinks"
}

import {
  to = cloudflare_workers_route.staging_assetlinks
  id = "3f80ecc2735314886702b6643b15d150/034722be6be8417b91520811fc40d280"
}

resource "cloudflare_workers_script" "staging_assetlinks" {
  account_id         = "29945c90bc934c629c8e5a11cbfd146b"
  script_name        = "orbit-staging-assetlinks"
  main_module        = "staging-assetlinks.mjs"
  content            = file("${path.module}/workers/staging-assetlinks.mjs")
  compatibility_date = "2026-10-12"
}

resource "cloudflare_workers_route" "staging_assetlinks" {
  zone_id = cloudflare_zone.orbit.id
  pattern = "app-staging.useorbit.org/.well-known/assetlinks.json"
  script  = cloudflare_workers_script.staging_assetlinks.script_name
}
