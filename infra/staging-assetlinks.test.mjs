import assert from "node:assert/strict"
import { readFileSync } from "node:fs"
import { test } from "node:test"
import worker from "./workers/staging-assetlinks.mjs"

const url = "https://app-staging.useorbit.org/.well-known/assetlinks.json"
const source = readFileSync(new URL("./staging-assetlinks.tf", import.meta.url), "utf8")

test("answers GET with both packages and the static edge header", async () => {
  const response = worker.fetch(new Request(url))
  assert.equal(response.status, 200)
  assert.equal(response.headers.get("content-type"), "application/json")
  assert.equal(response.headers.get("x-orbit-association-source"), "static-edge")
  const links = await response.json()
  assert.deepEqual(links.map(link => link.target.package_name), ["org.useorbit.app", "org.useorbit.app.staging"])
})

test("answers HEAD with no body and rejects other methods", async () => {
  const head = worker.fetch(new Request(url, { method: "HEAD" }))
  assert.equal(head.status, 200)
  assert.equal(await head.text(), "")
  const post = worker.fetch(new Request(url, { method: "POST" }))
  assert.equal(post.status, 405)
  assert.equal(post.headers.get("allow"), "GET, HEAD")
})

test("Terraform imports the live script and route and routes only the assetlinks path", () => {
  assert.match(source, /id = "29945c90bc934c629c8e5a11cbfd146b\/orbit-staging-assetlinks"/)
  assert.match(source, /id = "3f80ecc2735314886702b6643b15d150\/034722be6be8417b91520811fc40d280"/)
  assert.match(source, /pattern = "app-staging\.useorbit\.org\/\.well-known\/assetlinks\.json"/)
  assert.match(source, /script\s+= cloudflare_workers_script\.staging_assetlinks\.script_name/)
  assert.match(readFileSync(new URL("./cloudflare.tf", import.meta.url), "utf8"), /app_staging = \{[^}]*proxied = true/)
})
