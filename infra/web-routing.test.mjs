import assert from "node:assert/strict"
import { after, test } from "node:test"
import { fileURLToPath } from "node:url"
import { Miniflare, Response } from "miniflare"

const publicOrigin = "https://app.useorbit.org"
const redesignHost = "orbit-web-redesign.onrender.com"
let upstream
const worker = new Miniflare({
  modules: true,
  cf: false,
  scriptPath: fileURLToPath(new URL("./workers/web-routing.mjs", import.meta.url)),
  compatibilityDate: "2026-07-30",
  bindings: { REDESIGN_HOST: redesignHost },
  outboundService: request => upstream(request),
})
after(() => worker.dispose())

for (const [cookie, redesign] of [
  [undefined, false],
  ["orbit_design=redesign", true],
  ["session=opaque; orbit_design=redesign; theme=dark", true],
  ["orbit_design=current", false],
  ["orbit_design=", false],
  ["other_orbit_design=redesign", false],
  ["orbit_design=redesign-extra", false],
  ["orbit_design=Redesign", false],
]) {
  test(`routes cookie ${cookie ?? "absent"} to ${redesign ? "redesign" : "current"}`, async () => {
    const origin = redesign ? `https://${redesignHost}` : publicOrigin
    let calls = 0
    upstream = request => {
      calls++
      assert.equal(request.url, `${origin}/api/health?probe=routing`)
      assert.equal(request.method, "GET")
      return new Response(origin)
    }
    const response = await worker.dispatchFetch(`${publicOrigin}/api/health?probe=routing`, {
      headers: cookie ? { Cookie: cookie } : {},
    })
    assert.equal(await response.text(), origin)
    assert.equal(calls, 1)
  })
}

for (const redesign of [false, true]) {
  test(`preserves a Server Action on ${redesign ? "redesign" : "current"} with the required Host`, async () => {
    const origin = redesign ? `https://${redesignHost}` : publicOrigin
    const path = "/actions/save?return=%2Fprofile&tag=a&tag=b"
    const headers = {
      Cookie: `session=opaque; orbit_design=${redesign ? "redesign" : "current"}`,
      Authorization: "Bearer test-token",
      Origin: publicOrigin,
      "Content-Type": "text/plain;charset=UTF-8",
      "Next-Action": "action-id",
      "Next-Router-State-Tree": "%5B%22%22%5D",
      "X-Custom-Header": "retained",
      "X-Forwarded-Host": "incoming.example",
      "X-Forwarded-Proto": "http",
    }
    const responseHeaders = {
      Location: `${publicOrigin}/profile`,
      "Set-Cookie": "session=renewed; Path=/; Secure; HttpOnly; SameSite=Lax",
      "Cache-Control": "private, no-store",
      "X-Action-Redirect": "/profile;push",
    }
    let calls = 0
    upstream = async request => {
      calls++
      assert.equal(request.url, `${origin}${path}`)
      assert.equal(request.method, "POST")
      assert.equal(await request.text(), '["payload"]')
      for (const [key, value] of Object.entries(headers)) {
        if (!key.startsWith("X-Forwarded-")) assert.equal(request.headers.get(key), value)
      }
      assert.equal(request.headers.get("Host"), redesign ? redesignHost : "app.useorbit.org")
      assert.equal(request.headers.get("X-Forwarded-Host"), redesign ? "app.useorbit.org" : "incoming.example")
      assert.equal(request.headers.get("X-Forwarded-Proto"), redesign ? "https" : "http")
      return new Response("saved", { status: 303, headers: responseHeaders })
    }
    const response = await worker.dispatchFetch(`${publicOrigin}${path}`, {
      method: "POST", headers, body: '["payload"]', redirect: "manual",
    })
    assert.equal(response.status, 303, await response.clone().text())
    assert.equal(await response.text(), "saved")
    for (const [key, value] of Object.entries(responseHeaders)) assert.equal(response.headers.get(key), value)
    assert.equal(calls, 1)
  })

  test(`passes the auth callback query through ${redesign ? "redesign" : "current"}`, async () => {
    const origin = redesign ? `https://${redesignHost}` : publicOrigin
    const path = "/auth-callback?code=opaque%2Bcode&state=opaque%2Fstate"
    let calls = 0
    upstream = request => {
      calls++
      assert.equal(request.url, `${origin}${path}`)
      assert.equal(request.method, "GET")
      return new Response(null, { status: 302, headers: { Location: `${publicOrigin}/today` } })
    }
    const response = await worker.dispatchFetch(`${publicOrigin}${path}`, {
      headers: { Cookie: redesign ? "orbit_design=redesign" : "session=opaque" },
      redirect: "manual",
    })
    assert.equal(response.status, 302, await response.clone().text())
    assert.equal(response.headers.get("Location"), `${publicOrigin}/today`)
    assert.equal(calls, 1)
  })
}

test("removing the design cookie returns the same path to the current build", async () => {
  for (const redesign of [true, false]) {
    const origin = redesign ? `https://${redesignHost}` : publicOrigin
    upstream = request => {
      assert.equal(request.url, `${origin}/today`)
      return new Response(origin)
    }
    const response = await worker.dispatchFetch(`${publicOrigin}/today`, {
      headers: redesign ? { Cookie: "orbit_design=redesign" } : {},
    })
    assert.equal(await response.text(), origin)
  }
})
