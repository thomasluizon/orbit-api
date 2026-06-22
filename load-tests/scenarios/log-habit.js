import http from 'k6/http';
import { check, fail } from 'k6';
import { BASE_URL, requireProdOptIn, readNumber, readString } from '../lib/config.js';
import { acquireSession, authHeaders } from '../lib/auth.js';
import { abortThresholds, recordRateLimit } from '../lib/thresholds.js';
import { summarizeScenario } from '../lib/summary.js';

const PEAK_RPS = readNumber('PEAK_RPS', 3);

export const options = {
  scenarios: {
    log_habit: {
      executor: 'ramping-arrival-rate',
      startRate: 1,
      timeUnit: '1s',
      preAllocatedVUs: 10,
      maxVUs: 20,
      stages: [
        { target: 1, duration: '30s' },
        { target: PEAK_RPS, duration: '1m' },
        { target: 0, duration: '30s' },
      ],
    },
  },
  thresholds: abortThresholds(),
};

export function setup() {
  requireProdOptIn();
  const habitId = readString('HABIT_ID', '');
  if (!habitId) {
    fail('HABIT_ID is required for the log-habit scenario. See load-tests/README.md (use a dedicated FLEXIBLE habit).');
  }
  return { session: acquireSession(), habitId, logDate: readString('LOG_DATE', '') };
}

export default function (data) {
  const url = `${BASE_URL}/api/habits/${data.habitId}/log`;
  const body = data.logDate ? JSON.stringify({ date: data.logDate }) : '{}';
  const response = http.post(url, body, {
    headers: { ...authHeaders(data.session), 'Content-Type': 'application/json' },
    tags: { name: 'log_habit' },
  });
  recordRateLimit(response);
  check(response, {
    'status is 200': (r) => r.status === 200,
    'not server error': (r) => r.status < 500,
  });
}

export function handleSummary(data) {
  return summarizeScenario('log-habit', data);
}
