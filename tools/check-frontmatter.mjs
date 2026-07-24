#!/usr/bin/env node
/**
 * Gate: every skill and agent definition must have parseable YAML frontmatter.
 *
 * An unquoted YAML scalar containing ": " (colon followed by whitespace) breaks
 * the parse. The file then loads with NO description, or does not load at all,
 * and nothing reports an error. Measured 2026-07-24 in orbit-ui-mobile: this
 * silently disabled five skills and the `security-reviewer` agent, which is one
 * of the subagents `/pr-review` orchestrates, so its security pass had been a
 * no-op. This repo's own `security-reviewer` carried the same defect and was
 * hand-fixed; this gate is what stops the ninth recurrence.
 *
 * The fix is always the same: make the value a folded block scalar.
 *
 *   description: >-
 *     Text that: contains a colon.
 *
 * Vendored from orbit-ui-mobile/tools/check-frontmatter.mjs. This repo has no
 * Node toolchain and no package.json, so the script stays dependency-free and
 * CI installs Node itself. One deliberate divergence from the upstream copy: a
 * declared root that does not exist is an error here rather than a quiet skip.
 * All three roots are tracked directories, so a missing one means the gate has
 * become a no-op, and a verifier that cannot look must never report clean.
 *
 * Usage: node tools/check-frontmatter.mjs [--fix]
 * Exit 0 clean, 1 on any offender.
 */

import fs from 'node:fs';
import path from 'node:path';

const ROOTS = ['.claude/agents', '.claude/skills', '.opencode/agents'];
const BLOCK_SCALAR = /^[>|][-+]?\d*$/;

const missingRoots = ROOTS.filter((root) => !fs.existsSync(root));
if (missingRoots.length > 0) {
  console.error(`Frontmatter roots missing, so the gate cannot verify anything:`);
  for (const root of missingRoots) console.error(`  ${root}`);
  console.error('\nRun this from the orbit-api repo root. If a root was deliberately');
  console.error('removed, delete it from ROOTS in this script in the same change.');
  process.exit(1);
}

const targets = [];
for (const root of ROOTS) {
  const walk = (dir) => {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) walk(full);
      else if (entry.name.endsWith('.md')) targets.push(full);
    }
  };
  walk(root);
}

const offenders = [];
for (const file of targets) {
  const text = fs.readFileSync(file, 'utf8');
  const match = text.match(/^---\r?\n([\s\S]*?)\r?\n---/);
  if (!match) continue;
  for (const raw of match[1].split(/\r?\n/)) {
    const line = raw.endsWith('\r') ? raw.slice(0, -1) : raw;
    const kv = line.match(/^([A-Za-z][\w-]*):[ \t](.*)$/);
    if (!kv) continue;
    const value = kv[2].trim();
    if (!value) continue;
    if (BLOCK_SCALAR.test(value)) continue;
    if (/^"[^"]*"$/.test(value) || /^'[^']*'$/.test(value)) continue;
    if (/^\[.*\]$/.test(value) || /^\{.*\}$/.test(value)) continue;
    if (/:[ \t]/.test(value)) offenders.push({ file, key: kv[1], value });
  }
}

if (offenders.length === 0) {
  console.log(`frontmatter ok: ${targets.length} skill and agent files parse`);
  process.exit(0);
}

if (process.argv.includes('--fix')) {
  const byFile = new Map();
  for (const o of offenders) byFile.set(o.file, [...(byFile.get(o.file) ?? []), o]);
  for (const [file, items] of byFile) {
    const text = fs.readFileSync(file, 'utf8');
    const eol = text.includes('\r\n') ? '\r\n' : '\n';
    const lines = text.split(/\r?\n/);
    for (const { key, value } of items) {
      const index = lines.findIndex((line) => line.startsWith(`${key}: `));
      if (index !== -1) lines.splice(index, 1, `${key}: >-`, `  ${value}`);
    }
    fs.writeFileSync(file, lines.join(eol));
    console.log(`fixed ${file}`);
  }
  process.exit(0);
}

console.error(`Unparseable frontmatter in ${offenders.length} file(s).`);
console.error('A ": " inside an unquoted YAML value breaks the parse, so the skill or agent');
console.error('loads with no description or does not load at all, silently.\n');
for (const { file, key } of offenders) console.error(`  ${file}  [${key}]`);
console.error('\nFix: make the value a folded block scalar, or run:');
console.error('  node tools/check-frontmatter.mjs --fix');
process.exit(1);
