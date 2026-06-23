import http from 'k6/http';
import { check } from 'k6';
import { BASE_URL, requireProdOptIn, readNumber } from '../lib/config.js';
import { acquireSession, authHeaders } from '../lib/auth.js';
import { abortThresholds, recordRateLimit } from '../lib/thresholds.js';
import { summarizeScenario } from '../lib/summary.js';

const PEAK_RPS = readNumber('PEAK_RPS', 5);
const PAGE_SIZE = readNumber('PAGE_SIZE', 50);

export const options = {
  scenarios: {
    habits_list: {
      executor: 'ramping-arrival-rate',
      startRate: 1,
      timeUnit: '1s',
      preAllocatedVUs: 10,
      maxVUs: 30,
      stages: [
        { target: 1, duration: '30s' },
        { target: PEAK_RPS, duration: '1m' },
        { target: PEAK_RPS, duration: '1m' },
        { target: 0, duration: '30s' },
      ],
    },
  },
  thresholds: abortThresholds(),
};

export function setup() {
  requireProdOptIn();
  return acquireSession();
}

export default function (session) {
  const today = new Date().toISOString().slice(0, 10);
  const url = `${BASE_URL}/api/habits?dateFrom=${today}&dateTo=${today}&includeOverdue=true&page=1&pageSize=${PAGE_SIZE}`;
  const response = http.get(url, { headers: authHeaders(session), tags: { name: 'habits_list' } });
  recordRateLimit(response);
  check(response, {
    'status is 200': (r) => r.status === 200,
    'not server error': (r) => r.status < 500,
  });
}

export function handleSummary(data) {
  return summarizeScenario('habits-list', data);
}
