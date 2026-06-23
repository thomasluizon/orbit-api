import http from 'k6/http';
import { check } from 'k6';
import { BASE_URL, requireProdOptIn, readNumber, readString } from '../lib/config.js';
import { acquireSession, authHeaders } from '../lib/auth.js';
import { abortThresholds, recordRateLimit } from '../lib/thresholds.js';
import { summarizeScenario } from '../lib/summary.js';

const CHAT_RPM = readNumber('CHAT_RPM', 10);
const CHAT_MESSAGE = readString('CHAT_MESSAGE', 'Hi, just saying hello — no action needed.');

export const options = {
  scenarios: {
    chat: {
      executor: 'constant-arrival-rate',
      rate: CHAT_RPM,
      timeUnit: '1m',
      duration: '2m',
      preAllocatedVUs: 3,
      maxVUs: 5,
    },
  },
  thresholds: abortThresholds({ maxP95: 8000, maxP99: 15000 }),
};

export function setup() {
  requireProdOptIn();
  return acquireSession();
}

export default function (session) {
  const response = http.post(
    `${BASE_URL}/api/chat`,
    { message: CHAT_MESSAGE },
    { headers: authHeaders(session), tags: { name: 'chat' } }
  );
  recordRateLimit(response);
  check(response, {
    'status is 200/403/429': (r) => r.status === 200 || r.status === 403 || r.status === 429,
    'not server error': (r) => r.status < 500,
  });
}

export function handleSummary(data) {
  return summarizeScenario('chat', data);
}
