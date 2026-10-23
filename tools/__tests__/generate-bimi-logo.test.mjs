import assert from "node:assert/strict"
import { spawnSync } from "node:child_process"
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { test } from "node:test"

const project = new URL("../../", import.meta.url)
const asset = new URL("src/Orbit.Api/Branding/Assets/orbit-bimi.svg", project)

function fixture(t) {
  const root = mkdtempSync(join(tmpdir(), "orbit-bimi-"))
  t.after(() => rmSync(root, { recursive: true, force: true }))
  const branding = join(root, "src/Orbit.Api/Branding")
  mkdirSync(join(root, "tools"))
  mkdirSync(join(branding, "Sources"), { recursive: true })
  mkdirSync(join(branding, "Assets"))
  copyFileSync(new URL("tools/generate-bimi-logo.mjs", project), join(root, "tools/generate-bimi-logo.mjs"))
  copyFileSync(new URL("src/Orbit.Api/Branding/Sources/orbit-mark.svg", project), join(branding, "Sources/orbit-mark.svg"))
  copyFileSync(asset, join(branding, "Assets/orbit-bimi.svg"))
  return {
    asset: join(branding, "Assets/orbit-bimi.svg"),
    source: join(branding, "Sources/orbit-mark.svg"),
    run: (...args) => spawnSync(process.execPath, [join(root, "tools/generate-bimi-logo.mjs"), ...args], {
      cwd: tmpdir(), encoding: "utf8",
    }),
  }
}

test("regenerating the canonical source reproduces the committed BIMI asset byte for byte", t => {
  const generated = fixture(t)
  writeFileSync(generated.asset, "replace this asset\n")
  const result = generated.run()
  assert.equal(result.status, 0, result.stderr)
  assert.deepEqual(readFileSync(generated.asset), readFileSync(asset))
})

test("--check accepts the committed asset and rejects drift without rewriting it", t => {
  const generated = fixture(t)
  assert.equal(generated.run("--check").status, 0)
  const changed = readFileSync(generated.asset, "utf8").replace("<title>Orbit</title>", "<title>Changed</title>")
  writeFileSync(generated.asset, changed)
  const result = generated.run("--check")
  assert.equal(result.status, 1)
  assert.match(result.stderr, /committed BIMI icon differs/)
  assert.equal(readFileSync(generated.asset, "utf8"), changed)
})

test("the generated output keeps the SVG Tiny PS profile and opaque square canvas", t => {
  const generated = fixture(t)
  const result = generated.run()
  assert.equal(result.status, 0, result.stderr)
  const bytes = readFileSync(generated.asset)
  const svg = bytes.toString("utf8")
  assert.ok(bytes.length <= 32000)
  assert.match(svg, /^<\?xml version='1.0' encoding='utf-8'\?>\n<svg xmlns="http:\/\/www.w3.org\/2000\/svg" version="1.2" baseProfile="tiny-ps" width="1024" height="1024" viewBox="0 0 1024 1024">/)
  assert.match(svg, /<title>Orbit<\/title>/)
  assert.match(svg, /<desc>[^<]+<\/desc>/)
  assert.match(svg, /<rect width="1024" height="1024" fill="#09090B" \/>/)
  assert.deepEqual([...svg.matchAll(/<(?![?/!])([\w:-]+)\b/g)].map(match => match[1]),
    ["svg", "title", "desc", "rect", "path", "path"])
  assert.doesNotMatch(svg, /<!|<\?(?!xml\s)|\b(?:href|src|on\w+)\s*=|url\s*\(/i)
  for (const [, attributes] of svg.matchAll(/<path ([^>]+)\/>/g)) {
    assert.match(attributes, /^fill="#[A-F0-9]{6}" transform="translate\(-?[\d.]+ -?[\d.]+\) scale\([\d.]+\)" d="[MLCZ\d. ,+-]+"(?: fill-rule="evenodd")? $/)
  }
})

test("the generator rejects unsupported path commands and a noncanonical canvas", t => {
  const generated = fixture(t)
  const source = readFileSync(generated.source, "utf8")
  writeFileSync(generated.source, source.replace('d="M', 'd="m'))
  const invalidPath = generated.run()
  assert.equal(invalidPath.status, 1)
  assert.match(invalidPath.stderr, /absolute M, L, C and Z commands/)
  writeFileSync(generated.source, source.replace('viewBox="0 0 1024 1024"', 'viewBox="0 0 512 1024"'))
  const invalidCanvas = generated.run()
  assert.equal(invalidCanvas.status, 1)
  assert.match(invalidCanvas.stderr, /canonical Orbit body and moon on a 1024 canvas/)
})
