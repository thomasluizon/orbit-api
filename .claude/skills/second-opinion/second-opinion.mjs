#!/usr/bin/env node
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';

const DEFAULT_MODEL = 'opencode-go/glm-5.2';
const DEFAULT_TIMEOUT_MS = 180_000;
const SLUG = /^[\w./:-]+$/;

/**
 * Parse `--model <slug>` and `--timeout <ms>` from argv, falling back to defaults.
 * @returns {{ model: string, timeout: number }}
 */
function parseArgs(argv) {
  let model = DEFAULT_MODEL;
  let timeout = DEFAULT_TIMEOUT_MS;
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--model' && argv[i + 1]) model = argv[++i];
    else if (argv[i] === '--timeout' && argv[i + 1]) timeout = Number(argv[++i]) || DEFAULT_TIMEOUT_MS;
  }
  if (!SLUG.test(model)) model = DEFAULT_MODEL;
  return { model, timeout };
}

function readStdin() {
  try {
    return readFileSync(0, 'utf8');
  } catch {
    return '';
  }
}

function buildPrompt(finding) {
  return [
    'You are an INDEPENDENT second-opinion code reviewer — a different model from the one that raised this finding.',
    'A primary reviewer flagged a CRITICAL issue in a pull request. Decide whether it is a REAL, blast-radius-carrying',
    'defect in the changed code, or a FALSE POSITIVE.',
    '',
    'Rules:',
    '- Judge ONLY from the finding text and code below. Do NOT use any tools, do NOT read files, do NOT ask questions.',
    '- Be skeptical in BOTH directions: do not rubber-stamp, do not reflexively contradict.',
    '- AGREE if the cited defect is real and the Critical severity is justified.',
    '- DISAGREE if the code is actually correct, the path unreachable, the value already validated, the severity',
    '  inflated, or the claim unsupported by the shown code.',
    '- UNSURE only if the given context genuinely cannot decide it.',
    '',
    'Output ONLY one line of JSON — no prose, no code fences:',
    '{"verdict":"AGREE"|"DISAGREE"|"UNSURE","confidence":"high"|"medium"|"low","reasoning":"<= 2 sentences citing the specific code"}',
    '',
    '--- FINDING ---',
    finding.trim(),
    '--- END FINDING ---',
  ].join('\n');
}

/** Extract the assistant text from opencode's `--format json` JSONL event stream. */
function parseEvents(stdout) {
  let text = '';
  let errorMessage = null;
  for (const line of String(stdout).split(/\r?\n/)) {
    const trimmed = line.trim();
    if (!trimmed) continue;
    let event;
    try {
      event = JSON.parse(trimmed);
    } catch {
      continue;
    }
    if (event.type === 'text' && event.part?.text) text += event.part.text;
    else if (event.type === 'error') errorMessage = event.error?.data?.message || event.error?.name || 'opencode error';
  }
  return { text: text.trim(), errorMessage };
}

/** Pull the verdict object out of the model's reply, tolerating code fences and surrounding prose. */
function parseVerdict(text) {
  const candidates = [...text.matchAll(/\{[\s\S]*?\}/g)].map((match) => match[0]).reverse();
  for (const candidate of candidates) {
    try {
      const parsed = JSON.parse(candidate);
      const verdict = String(parsed.verdict || '').toUpperCase();
      if (verdict === 'AGREE' || verdict === 'DISAGREE' || verdict === 'UNSURE') {
        return {
          verdict,
          confidence: String(parsed.confidence || 'unknown').toLowerCase(),
          reasoning: String(parsed.reasoning || '').slice(0, 600),
        };
      }
    } catch {
      continue;
    }
  }
  return null;
}

function emit(result) {
  process.stdout.write(JSON.stringify(result) + '\n');
  process.exit(0);
}

const { model, timeout } = parseArgs(process.argv.slice(2));
const finding = readStdin();

if (!finding.trim()) emit({ status: 'UNAVAILABLE', reason: 'no finding text on stdin', model });

const run = spawnSync('opencode', ['run', '--model', model, '--format', 'json'], {
  input: buildPrompt(finding),
  encoding: 'utf8',
  timeout,
  maxBuffer: 32 * 1024 * 1024,
  shell: true,
});

if (run.error) {
  const reason = run.error.code === 'ETIMEDOUT' ? 'opencode timed out' : `opencode not runnable (${run.error.code})`;
  emit({ status: 'UNAVAILABLE', reason, model });
}

const { text, errorMessage } = parseEvents(run.stdout);
if (errorMessage) emit({ status: 'UNAVAILABLE', reason: errorMessage, model });
if (!text) {
  const stderrTail = String(run.stderr || '').trim().slice(-200);
  emit({ status: 'UNAVAILABLE', reason: stderrTail || 'empty response from opencode', model });
}

const verdict = parseVerdict(text);
if (!verdict) emit({ status: 'UNAVAILABLE', reason: 'unparseable verdict', model, raw: text.slice(0, 300) });

emit({ status: 'OK', ...verdict, model });
