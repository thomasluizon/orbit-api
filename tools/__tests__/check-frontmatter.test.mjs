import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';

const script = fileURLToPath(new URL('../check-frontmatter.mjs', import.meta.url));
function run(content, ...args) {
  const root = mkdtempSync(join(tmpdir(), 'orbit-frontmatter-'));
  try {
    for (const dir of ['.claude/agents', '.claude/skills', '.opencode/agents'])
      mkdirSync(join(root, dir), { recursive: true });
    writeFileSync(join(root, '.claude/agents/sample.md'), content);
    return spawnSync(process.execPath, [script, ...args], { cwd: root, encoding: 'utf8' });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test('accepts complete frontmatter and markdown without frontmatter', () => {
  assert.equal(run('---\nname: example\n---\nBody\n').status, 0);
  assert.equal(run('# Example\n').status, 0);
});

test('rejects an opening fence without a valid closing fence', () => {
  for (const suffix of ['', '\n---broken']) {
    const result = run(`---\nname: example${suffix}`);
    assert.equal(result.status, 1, result.stdout);
    assert.match(result.stderr, /closing fence/);
  }
});

test('rejects a BOM before the opening fence', () => {
  assert.equal(run('\uFEFF---\nname: example\n---\n').status, 1);
});

test('fix verifies again and fails when an offender cannot be repaired', () => {
  const result = run('---\nname: example\n', '--fix');
  assert.equal(result.status, 1, result.stdout);
  assert.match(result.stderr, /closing fence/);
  assert.equal(run('---\ndescription: example: detail\n---\n', '--fix').status, 0);
});
