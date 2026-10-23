import { readFileSync, writeFileSync } from "node:fs"

function curveExtrema([p0, p1, p2, p3]) {
  const a = -p0 + 3 * p1 - 3 * p2 + p3
  const b = 2 * (p0 - 2 * p1 + p2)
  const c = p1 - p0
  let roots = []
  if (Math.abs(a) < 1e-12) {
    if (Math.abs(b) > 1e-12) roots = [-c / b]
  } else if (b * b - 4 * a * c >= 0) {
    const delta = Math.sqrt(b * b - 4 * a * c)
    roots = [(-b + delta) / (2 * a), (-b - delta) / (2 * a)]
  }
  return [p0, p3, ...roots.filter(t => t > 0 && t < 1).map(t =>
    (1 - t) ** 3 * p0 + 3 * (1 - t) ** 2 * t * p1 + 3 * (1 - t) * t ** 2 * p2 + t ** 3 * p3)]
}

function inkBounds(paths) {
  const axes = [[], []]
  for (const path of paths) {
    const data = path.d
    if (!data || /[^MLCZ\d.\s,+-]/.test(data)) {
      throw new Error("The canonical mark must use absolute M, L, C and Z commands.")
    }
    const tokens = data.match(/[MLCZ]|[-+]?\d*\.?\d+/g)
    let current = [0, 0]
    let start = current
    for (let index = 0; index < tokens.length;) {
      const command = tokens[index++]
      if (command === "Z") {
        current = start
        continue
      }
      const count = { M: 2, L: 2, C: 6 }[command]
      const values = tokens.slice(index, index + count).map(Number)
      if (!count || values.length !== count || values.some(value => !Number.isFinite(value))) {
        throw new Error("The canonical mark has an invalid path command.")
      }
      index += count
      const end = values.slice(-2)
      for (let axis = 0; axis < 2; axis++) {
        axes[axis].push(...(command === "C"
          ? curveExtrema([current[axis], ...values.filter((_, i) => i % 2 === axis)])
          : [end[axis]]))
      }
      current = end
      if (command === "M") start = end
    }
  }
  return axes.map(axis => [Math.min(...axis), Math.max(...axis)])
}

function attributes(tag) {
  return Object.fromEntries([...tag.matchAll(/([\w-]+)="([^"]*)"/g)].map(match => [match[1], match[2]]))
}

const args = process.argv.slice(2)
if (args.length && (args.length !== 1 || args[0] !== "--check")) {
  console.error("Usage: node tools/generate-bimi-logo.mjs [--check]")
  process.exit(1)
}

const directory = new URL("../src/Orbit.Api/Branding/", import.meta.url)
const source = readFileSync(new URL("Sources/orbit-mark.svg", directory), "utf8")
const root = attributes(source.match(/<svg\b[^>]*>/)?.[0] ?? "")
const paths = [...source.matchAll(/<path\b[^>]*\/>/g)].map(match => attributes(match[0]))
if (paths.length !== 2 || root.viewBox !== "0 0 1024 1024" || root.xmlns !== "http://www.w3.org/2000/svg") {
  throw new Error("Expected the canonical Orbit body and moon on a 1024 canvas.")
}
const [[left, right], [top, bottom]] = inkBounds(paths)
const scale = 1024 * 0.6 / (right - left)
const dx = 512 - scale * (left + right) / 2
const dy = 512 - scale * (top + bottom) / 2
const transform = `translate(${dx.toFixed(12)} ${dy.toFixed(12)}) scale(${scale.toFixed(12)})`
const colors = ["#F4F4F6", "#C4530F"]
const content = `<?xml version='1.0' encoding='utf-8'?>
<svg xmlns="http://www.w3.org/2000/svg" version="1.2" baseProfile="tiny-ps" width="1024" height="1024" viewBox="0 0 1024 1024">
  <title>Orbit</title>
  <desc>Orbit's ringed planet and orange moon on a solid dark background.</desc>
  <rect width="1024" height="1024" fill="#09090B" />
${paths.map((path, index) => `  <path fill="${colors[index]}" transform="${transform}" d="${path.d}"${path["fill-rule"] ? ` fill-rule="${path["fill-rule"]}"` : ""} />`).join("\n")}
</svg>
`
const destination = new URL("Assets/orbit-bimi.svg", directory)
if (args[0] === "--check") {
  if (!readFileSync(destination).equals(Buffer.from(content))) {
    console.error("The committed BIMI icon differs from its generated source.")
    process.exit(1)
  }
} else {
  writeFileSync(destination, content)
}
console.log(`BIMI icon: ${Buffer.byteLength(content)} bytes`)
