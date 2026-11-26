import assert from "node:assert/strict"
import { spawnSync } from "node:child_process"
import { readFileSync } from "node:fs"
import { join, resolve } from "node:path"
import { test } from "node:test"
import { buildBimi, readMark } from "../generate-bimi-logo.mjs"

const root = resolve(import.meta.dirname, "../..")
const branding = join(root, "src/Orbit.Api/Branding")
const mark = readFileSync(join(branding, "Sources/orbit-mark.svg"), "utf8")
const wrap = body => `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10">${body}</svg>`

test("the committed BIMI icon is what the generator builds from the committed mascot", () => {
  assert.equal(buildBimi(mark).content, readFileSync(join(branding, "Assets/orbit-bimi.svg"), "utf8"))
})

test("the icon draws every mascot shape with plain paths in the Tiny PS profile", () => {
  const { content, shapes } = buildBimi(mark)
  assert.equal(shapes.length, 7)
  assert.match(content, /baseProfile="tiny-ps"/)
  assert.doesNotMatch(content, /<(circle|clipPath|style|defs|g|use)\b|var\(|currentColor|clip-path/)
  for (const shape of shapes) assert.match(shape.d, /^[MLCZ0-9. ,+-]+$/)
  assert.deepEqual([...new Set(shapes.map(shape => shape.fill))].sort(), ["#09090B", "#C4530F", "#F4F4F6"])
  assert.equal(shapes.at(-1).fill, "#C4530F")
  assert.equal(shapes.filter(shape => shape.fill === "#C4530F").length, 1)
})

test("the ink is centred and spans 60 percent of the canvas width", () => {
  const { bounds, content, scale } = buildBimi(mark)
  const [dx, dy] = content.match(/translate\(([-\d.]+) ([-\d.]+)\)/).slice(1).map(Number)
  assert.ok(Math.abs(dx + scale * (bounds.left + bounds.right) / 2 - 512) < 1e-6)
  assert.ok(Math.abs(dy + scale * (bounds.top + bounds.bottom) / 2 - 512) < 1e-6)
  assert.ok(Math.abs(scale * (bounds.right - bounds.left) - 1024 * 0.6) < 1e-6)
})

test("a circle becomes four cubic curves whose extrema are the circle edges", () => {
  const { shapes } = readMark(wrap(`<circle cx="3" cy="4" r="2" fill="currentColor"/>`))
  assert.equal(shapes.length, 1)
  assert.equal((shapes[0].d.match(/C/g) ?? []).length, 4)
  const { bounds } = buildBimi(wrap(`<circle cx="3" cy="4" r="2" fill="currentColor"/>`))
  assert.deepEqual([bounds.left, bounds.right, bounds.top, bounds.bottom].map(value => Math.round(value * 1e6) / 1e6), [1, 5, 2, 6])
})

test("a path command outside M, L, C and Z is rejected", () => {
  assert.throws(() => buildBimi(wrap(`<path fill="currentColor" d="M0 0l1 1z"/>`)), /absolute M, L, C and Z/)
  assert.throws(() => buildBimi(wrap(`<path fill="currentColor" d="M0 0Q1 1 2 2Z"/>`)), /absolute M, L, C and Z/)
})

test("a fill that has no BIMI colour is rejected", () => {
  assert.throws(() => buildBimi(wrap(`<path fill="red" d="M0 0L1 1Z"/>`)), /unsupported fill/)
})

test("a clipped path that stays inside its clip circle is kept and one that leaves it is rejected", () => {
  const clip = `<defs><clipPath id="c"><circle cx="0" cy="0" r="10"/></clipPath></defs>`
  const inside = wrap(`${clip}<g clip-path="url(#c)"><path fill="var(--bg, #09090B)" d="M0 0L6 8L-6 8Z"/></g>`)
  assert.equal(readMark(inside).shapes.length, 1)
  const outside = wrap(`${clip}<g clip-path="url(#c)"><path fill="var(--bg, #09090B)" d="M0 0L12 8L-6 8Z"/></g>`)
  assert.throws(() => readMark(outside), /leaves its clip circle/)
})

test("the command line accepts only --check or --source with a file", () => {
  const run = (...args) => spawnSync(process.execPath, [join(root, "tools/generate-bimi-logo.mjs"), ...args], { encoding: "utf8" })
  assert.equal(run("--check").status, 0)
  assert.equal(run("--unknown").status, 1)
  assert.equal(run("--check", "--source").status, 1)
})
