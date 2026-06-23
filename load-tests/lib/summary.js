import { textSummary } from 'https://jslib.k6.io/k6-summary/0.1.0/index.js';

function metricValue(data, metricName, field) {
  const metric = data.metrics[metricName];
  if (!metric || !metric.values) {
    return null;
  }
  const value = metric.values[field];
  return value === undefined ? null : value;
}

function round(value, digits = 2) {
  return value === null ? null : Number(value.toFixed(digits));
}

function buildReport(scenarioName, data) {
  const httpReqs = metricValue(data, 'http_reqs', 'count') || 0;
  const durationField = 'http_req_duration';
  return {
    scenario: scenarioName,
    generatedAtUtc: new Date().toISOString(),
    throughput: {
      requests: round(httpReqs, 0),
      requestsPerSecond: round(metricValue(data, 'http_reqs', 'rate')),
    },
    latencyMs: {
      avg: round(metricValue(data, durationField, 'avg')),
      p95: round(metricValue(data, durationField, 'p(95)')),
      p99: round(metricValue(data, durationField, 'p(99)')),
      max: round(metricValue(data, durationField, 'max')),
    },
    errorRate: round(metricValue(data, 'http_req_failed', 'rate'), 4),
    checks: {
      passes: round(metricValue(data, 'checks', 'passes'), 0),
      fails: round(metricValue(data, 'checks', 'fails'), 0),
    },
    rateLimited429: round(metricValue(data, 'rate_limited_429', 'count'), 0),
  };
}

function toMarkdown(report) {
  const latency = report.latencyMs;
  return [
    `# Load test report — ${report.scenario}`,
    '',
    `Generated: ${report.generatedAtUtc}`,
    '',
    '| Metric | Value |',
    '| --- | --- |',
    `| Requests | ${report.throughput.requests} |`,
    `| Throughput (req/s) | ${report.throughput.requestsPerSecond} |`,
    `| Latency avg (ms) | ${latency.avg} |`,
    `| Latency p95 (ms) | ${latency.p95} |`,
    `| Latency p99 (ms) | ${latency.p99} |`,
    `| Latency max (ms) | ${latency.max} |`,
    `| Error rate | ${report.errorRate} |`,
    `| 429 rate-limited | ${report.rateLimited429} |`,
    `| Checks passed | ${report.checks.passes} |`,
    `| Checks failed | ${report.checks.fails} |`,
    '',
  ].join('\n');
}

export function summarizeScenario(scenarioName, data) {
  const report = buildReport(scenarioName, data);
  const base = `load-tests/results/${scenarioName}`;
  return {
    stdout: textSummary(data, { indent: ' ', enableColors: true }),
    [`${base}.summary.json`]: JSON.stringify(report, null, 2),
    [`${base}.summary.md`]: toMarkdown(report),
  };
}
