#!/usr/bin/env node
import { spawnSync } from "node:child_process"
import { readFileSync } from "node:fs"
import { resolve } from "node:path"

const checker = resolve(import.meta.dirname, "../../tools/check-timeless.mjs")
const result = spawnSync(process.execPath, [checker, "--hook"], {
  input: readFileSync(0), encoding: "utf8",
})
if (result.stderr) process.stderr.write(result.stderr)
process.exit(result.status ?? 2)
