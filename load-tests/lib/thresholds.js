import http from 'k6/http';
import { Counter } from 'k6/metrics';
import { readNumber } from './config.js';

http.setResponseCallback(http.expectedStatuses({ min: 200, max: 299 }, 403, 429));

export const rateLimited429 = new Counter('rate_limited_429');

export function recordRateLimit(response) {
  if (response.status === 429) {
    rateLimited429.add(1);
  }
}

export function abortThresholds(overrides = {}) {
  const maxErrorRate = readNumber('ABORT_ERROR_RATE', overrides.maxErrorRate ?? 0.1);
  const maxP95 = readNumber('ABORT_P95_MS', overrides.maxP95 ?? 1500);
  const maxP99 = readNumber('ABORT_P99_MS', overrides.maxP99 ?? 3000);

  return {
    http_req_failed: [{ threshold: `rate<${maxErrorRate}`, abortOnFail: true, delayAbortEval: '10s' }],
    http_req_duration: [
      { threshold: `p(95)<${maxP95}`, abortOnFail: true, delayAbortEval: '10s' },
      { threshold: `p(99)<${maxP99}`, abortOnFail: true, delayAbortEval: '10s' },
    ],
  };
}
