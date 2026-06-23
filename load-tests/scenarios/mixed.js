import http from 'k6/http';
import { check, fail } from 'k6';
import { BASE_URL, requireProdOptIn, readNumber, readString } from '../lib/config.js';
import { acquireSession, authHeaders } from '../lib/auth.js';
import { abortThresholds, recordRateLimit } from '../lib/thresholds.js';
import { summarizeScenario } from '../lib/summary.js';

const READ_RPS = readNumber('READ_RPS', 4);
const LOG_RPS = readNumber('LOG_RPS', 1);
const CHAT_RPM = readNumber('CHAT_RPM', 6);

export const options = {
  scenarios: {
    reads: {
      executor: 'ramping-arrival-rate',
      exec: 'habitsList',
      startRate: 1,
      timeUnit: '1s',
      preAllocatedVUs: 10,
      maxVUs: 30,
      stages: [
        { target: 1, duration: '30s' },
        { target: READ_RPS, duration: '2m' },
        { target: 0, duration: '30s' },
      ],
    },
    writes: {
      executor: 'constant-arrival-rate',
      exec: 'logHabit',
      rate: LOG_RPS,
      timeUnit: '1s',
      duration: '3m',
      preAllocatedVUs: 5,
      maxVUs: 15,
    },
    chat: {
      executor: 'constant-arrival-rate',
      exec: 'chat',
      rate: CHAT_RPM,
      timeUnit: '1m',
      duration: '3m',
      preAllocatedVUs: 3,
      maxVUs: 5,
    },
  },
  thresholds: abortThresholds(),
};

export function setup() {
  requireProdOptIn();
  const habitId = readString('HABIT_ID', '');
  if (!habitId) {
    fail('HABIT_ID is required for the mixed scenario (the writes stage logs a habit). See load-tests/README.md.');
  }
  return {
    session: acquireSession(),
    habitId,
    logDate: readString('LOG_DATE', ''),
    chatMessage: readString('CHAT_MESSAGE', 'Hi, just saying hello — no action needed.'),
  };
}

export function habitsList(data) {
  const today = new Date().toISOString().slice(0, 10);
  const url = `${BASE_URL}/api/habits?dateFrom=${today}&dateTo=${today}&includeOverdue=true&page=1&pageSize=50`;
  const response = http.get(url, { headers: authHeaders(data.session), tags: { name: 'habits_list' } });
  recordRateLimit(response);
  check(response, { 'list 200': (r) => r.status === 200, 'list not 5xx': (r) => r.status < 500 });
}

export function logHabit(data) {
  const url = `${BASE_URL}/api/habits/${data.habitId}/log`;
  const response = http.post(url, data.logDate ? JSON.stringify({ date: data.logDate }) : '{}', {
    headers: { ...authHeaders(data.session), 'Content-Type': 'application/json' },
    tags: { name: 'log_habit' },
  });
  recordRateLimit(response);
  check(response, { 'log 200': (r) => r.status === 200, 'log not 5xx': (r) => r.status < 500 });
}

export function chat(data) {
  const response = http.post(
    `${BASE_URL}/api/chat`,
    { message: data.chatMessage },
    { headers: authHeaders(data.session), tags: { name: 'chat' } }
  );
  recordRateLimit(response);
  check(response, { 'chat 200/403/429': (r) => r.status === 200 || r.status === 403 || r.status === 429, 'chat not 5xx': (r) => r.status < 500 });
}

export function handleSummary(data) {
  return summarizeScenario('mixed', data);
}
