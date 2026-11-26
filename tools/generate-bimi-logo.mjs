import { copyFileSync, readFileSync, writeFileSync } from "node:fs"
import { pathToFileURL } from "node:url"

const KAPPA = 0.5522847498307936
const CANVAS = 1024
const INK_WIDTH_RATIO = 0.6
const BACKGROUND = "#09090B"
const FILL_MAP = new Map([
  ["currentColor", "#F4F4F6"],
  ["var(--primary, currentColor)", "#C4530F"],
  ["var(--bg, #09090B)", BACKGROUND],
  ["var(--orbit-surface, var(--bg, #09090B))", BACKGROUND]
])

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

function parsePath(data) {
  if (!data || /[^MLCZ\d.\s,+-]/.test(data)) {
    throw new Error("The canonical mark must use absolute M, L, C and Z commands.")
  }
  const tokens = data.match(/[MLCZ]|[-+]?\d*\.?\d+/g)
  const segments = []
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
    segments.push({ command, from: current, values, end })
    current = end
    if (command === "M") start = end
  }
  return segments
}

function inkBounds(paths) {
  const axes = [[], []]
  for (const path of paths) {
    for (const { command, from, values, end } of parsePath(path.d)) {
      for (let axis = 0; axis < 2; axis++) {
        axes[axis].push(...(command === "C"
          ? curveExtrema([from[axis], ...values.filter((_, i) => i % 2 === axis)])
          : [end[axis]]))
      }
    }
  }
  return axes.map(axis => [Math.min(...axis), Math.max(...axis)])
}

function attributes(tag) {
  return Object.fromEntries([...tag.matchAll(/([\w-]+)="([^"]*)"/g)].map(match => [match[1], match[2]]))
}

function number(value) {
  return String(Number(value.toFixed(6)))
}

function circlePath({ cx, cy, r }) {
  const [x, y, radius] = [Number(cx), Number(cy), Number(r)]
  if (![x, y, radius].every(Number.isFinite) || radius <= 0) throw new Error("The canonical mark has an invalid circle.")
  const k = radius * KAPPA
  const p = (dx, dy) => `${number(x + dx)} ${number(y + dy)}`
  return `M${p(radius, 0)}C${p(radius, k)} ${p(k, radius)} ${p(0, radius)}`
    + `C${p(-k, radius)} ${p(-radius, k)} ${p(-radius, 0)}`
    + `C${p(-radius, -k)} ${p(-k, -radius)} ${p(0, -radius)}`
    + `C${p(k, -radius)} ${p(radius, -k)} ${p(radius, 0)}Z`
}

function assertInside(clip, path) {
  const radius = Number(clip.r)
  for (const { end } of parsePath(path.d)) {
    if (Math.hypot(end[0] - Number(clip.cx), end[1] - Number(clip.cy)) > radius + 0.01) {
      throw new Error("A clipped path leaves its clip circle, and the BIMI icon cannot draw a clip.")
    }
  }
}

export function readMark(source) {
  const root = attributes(source.match(/<svg\b[^>]*>/)?.[0] ?? "")
  if (root.xmlns !== "http://www.w3.org/2000/svg" || !root.viewBox) {
    throw new Error("Expected an SVG with a view box.")
  }
  const clips = new Map([...source.matchAll(/<clipPath\b[^>]*id="([^"]+)"[^>]*>\s*<circle\b([^>]*)\/>/g)]
    .map(match => [match[1], attributes(match[2])]))
  const body = source.replace(/<defs>[\s\S]*?<\/defs>/g, "")
  const shapes = []
  let clip = null
  for (const match of body.matchAll(/<g\b[^>]*>|<\/g>|<(path|circle)\b[^>]*\/>/g)) {
    if (match[0] === "</g>") {
      clip = null
    } else if (match[0].startsWith("<g")) {
      const reference = attributes(match[0])["clip-path"]?.match(/^url\(#([^)]+)\)$/)?.[1]
      clip = reference ? clips.get(reference) ?? null : null
      if (reference && !clip) throw new Error("The canonical mark uses a clip that is not a circle.")
    } else {
      const tag = attributes(match[0])
      const fill = FILL_MAP.get(tag.fill)
      if (!fill) throw new Error(`The canonical mark uses an unsupported fill: ${tag.fill}.`)
      const path = { d: match[1] === "circle" ? circlePath(tag) : tag.d, fill }
      if (tag["fill-rule"]) path.fillRule = tag["fill-rule"]
      if (clip) assertInside(clip, path)
      shapes.push(path)
    }
  }
  if (!shapes.length) throw new Error("The canonical mark has no shapes.")
  return { shapes, viewBox: root.viewBox }
}

export function buildBimi(source) {
  const { shapes } = readMark(source)
  const [[left, right], [top, bottom]] = inkBounds(shapes)
  const scale = CANVAS * INK_WIDTH_RATIO / (right - left)
  const dx = CANVAS / 2 - scale * (left + right) / 2
  const dy = CANVAS / 2 - scale * (top + bottom) / 2
  const transform = `translate(${dx.toFixed(12)} ${dy.toFixed(12)}) scale(${scale.toFixed(12)})`
  const content = `<?xml version='1.0' encoding='utf-8'?>
<svg xmlns="http://www.w3.org/2000/svg" version="1.2" baseProfile="tiny-ps" width="${CANVAS}" height="${CANVAS}" viewBox="0 0 ${CANVAS} ${CANVAS}">
  <title>Orbit</title>
  <desc>Orbit's mascot, a planet with two eyes, one ring and an orange moon, on a solid dark background.</desc>
  <rect width="${CANVAS}" height="${CANVAS}" fill="${BACKGROUND}" />
${shapes.map(shape => `  <path fill="${shape.fill}" transform="${transform}" d="${shape.d}"${shape.fillRule ? ` fill-rule="${shape.fillRule}"` : ""} />`).join("\n")}
</svg>
`
  return { content, bounds: { left, right, top, bottom }, scale, shapes }
}

function main(args) {
  const usage = "Usage: node tools/generate-bimi-logo.mjs [--check | --source <mascot.svg>]"
  const check = args.length === 1 && args[0] === "--check"
  const sourceOption = args.length === 2 && args[0] === "--source"
  if (args.length && !check && !sourceOption) {
    console.error(usage)
    process.exit(1)
  }
  const directory = new URL("../src/Orbit.Api/Branding/", import.meta.url)
  const markFile = new URL("Sources/orbit-mark.svg", directory)
  if (sourceOption) {
    readMark(readFileSync(args[1], "utf8"))
    copyFileSync(args[1], markFile)
  }
  const { content } = buildBimi(readFileSync(markFile, "utf8"))
  const destination = new URL("Assets/orbit-bimi.svg", directory)
  if (check) {
    if (!readFileSync(destination).equals(Buffer.from(content))) {
      console.error("The committed BIMI icon differs from its generated source.")
      process.exit(1)
    }
  } else {
    writeFileSync(destination, content)
  }
  console.log(`BIMI icon: ${Buffer.byteLength(content)} bytes`)
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) main(process.argv.slice(2))
