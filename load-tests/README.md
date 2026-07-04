# Orbit API — load / stress tests (k6)

Light, rate-capped load scripts for the hot money-and-retention endpoints, used **once** before scaling ad spend (issue #230). This is a pre-launch capacity probe, **not** ongoing load infrastructure.

> [!CAUTION]
> The QA environment was aborted, so the only realistic target is **production** (`https://api.useorbit.org`). These scripts are written to be safe, but a load test against prod is inherently risky. **Only ever run against prod inside an agreed off-peak, low-traffic window, with the abort thresholds enabled.** Never point this at prod during a rollout, a campaign, or peak hours. When in doubt, do not run.

## What it covers

| Scenario | Endpoint | Method | Throttle at the API |
| --- | --- | --- | --- |
| `habits-list` | `GET /api/habits` | GET | none (server-bound) |
| `log-habit` | `POST /api/habits/{id}/log` | POST | none (server-bound) |
| `chat` | `POST /api/chat` | POST (multipart) | **20 / min per user** + real OpenAI cost |
| `mixed` | all three blended | — | as above |

Auth is JWT Bearer. The scripts authenticate **once** in `setup()` and reuse the access token for every request — they do not log in per iteration.

## Install k6

k6 is a standalone Go binary (its scripts `import` from `k6`, so Node cannot run them).

- Windows: `winget install k6 --source winget` or `choco install k6`
- macOS: `brew install k6`
- Linux / CI: https://grafana.com/docs/k6/latest/set-up/install-k6/

Verify: `k6 version`.

## Safety mechanisms (baked into the scripts)

1. **Localhost-safe default.** With no `BASE_URL`, the target is `http://localhost:8080`. An accidental run cannot hit prod.
2. **Prod opt-in gate.** If `BASE_URL` resolves to `api.useorbit.org`, every scenario aborts in `setup()` unless `ALLOW_PROD=i-understand` is set. This is a deliberate speed bump, not security.
3. **Gentle ramp + low ceiling.** Open-model executors (`ramping-arrival-rate` / `constant-arrival-rate`) hold a *requested* RPS so latency blow-ups don't silently reduce load. Defaults are low (see below) and every rate is env-overridable.
4. **Abort thresholds = the kill-switch.** `thresholds` use `abortOnFail: true`, so k6 stops the whole run the moment the API degrades past the limit:
   - error rate ≥ `ABORT_ERROR_RATE` (default `0.10`)
   - p95 latency ≥ `ABORT_P95_MS` (default `1500`, chat `8000`)
   - p99 latency ≥ `ABORT_P99_MS` (default `3000`, chat `15000`)
   `delayAbortEval: 10s` avoids a false abort on the first cold-start request. To stop by hand at any time, `Ctrl-C` — k6 still writes the summary.
5. **429s and paygate 403s are counted, not fatal.** A response callback classifies 2xx, 403, and 429 as *expected*, so the kill-switch error rate (`http_req_failed`) reflects only genuine failures — 5xx and transport errors. Throttling (429) and quota (403) are tracked separately (`rate_limited_429` + the check counters) and never trip the abort on their own (the chat policy is *expected* to throttle at >20/min/user).

## Test account & tokens

Use a **dedicated throwaway account**, never a real user.

- **Non-prod target** (local): the `TEST_ACCOUNTS` env var (`email:code` pairs) makes the email code static, so the script can log in itself. Pass `TEST_EMAIL` + `TEST_CODE`. This bypass is **disabled when `ASPNETCORE_ENVIRONMENT=Production`**, so it does not work against prod.
- **Prod target:** the static-code bypass is off, and hammering `verify-code` would spam Resend and hit the 10/min auth limit. Instead, **log in once manually** (app or a single `verify-code` call with a real emailed code), copy the returned `token`, and pass it as `ACCESS_TOKEN`. The script reuses it directly.
  - The access token is a stateless JWT with a finite TTL (config-driven). Keep runs short (the defaults are 2–3 min) so the token can't expire mid-run; re-bootstrap if you run longer.
  - Do **not** try to drive load through `/api/auth/refresh`: refresh **rotates** (the presented refresh token is invalidated and a new one issued), so it can be used exactly once and is incompatible with reuse across virtual users.

### `log-habit` needs a habit id

`POST /api/habits/{id}/log` is a **toggle** for normal habits (log → unlog → log…) and rejects dates older than 7 days (`DefaultOverdueWindowDays`). To keep the write path clean and idempotent:

- Create one **dedicated flexible habit** on the test account and pass its id as `HABIT_ID`.
- Leave `LOG_DATE` unset — the script sends an empty body and the API logs against the user's *today*. For a flexible habit, repeat calls upsert the single (habit, today) row instead of toggling, so the data footprint is one log row.
- Each successful log still recalculates streak / XP / linked-goal progress for the test account. That is expected; it is the test account's own gamification state.

### `chat` costs real money

Every `/api/chat` request calls OpenAI (gpt-4.1-mini) — **real spend per request**, even for a one-word message. The default message is a no-action greeting, which will not trigger data-mutating tool calls (those route through the agent operation executor and need a confirmation-token round-trip for risky actions). Keep the chat rate far under the 20/min-per-user limit. A **free** test account is preferable (no Pro fact-extraction OpenAI calls); note a free account has an AI-message quota and will start returning paygate errors once exhausted — that 403 ceiling is itself the natural cap.

## Run it

All commands set the target explicitly and opt into prod. Drop `ALLOW_PROD` and point `BASE_URL` at localhost to rehearse safely first.

```bash
# Rehearse against a local API (no prod gate needed)
BASE_URL=http://localhost:8080 TEST_EMAIL=loadtest@example.com TEST_CODE=000000 \
  k6 run load-tests/scenarios/habits-list.js

# Prod, off-peak window only — reads (safest first)
BASE_URL=https://api.useorbit.org ALLOW_PROD=i-understand \
  ACCESS_TOKEN=eyJ... \
  k6 run load-tests/scenarios/habits-list.js

# Prod — writes (needs a dedicated flexible habit)
BASE_URL=https://api.useorbit.org ALLOW_PROD=i-understand \
  ACCESS_TOKEN=eyJ... HABIT_ID=<flexible-habit-guid> \
  k6 run load-tests/scenarios/log-habit.js

# Prod — chat (real OpenAI cost; keep it small)
BASE_URL=https://api.useorbit.org ALLOW_PROD=i-understand \
  ACCESS_TOKEN=eyJ... CHAT_RPM=6 \
  k6 run load-tests/scenarios/chat.js

# Prod — blended, most representative "safe concurrent users" probe
BASE_URL=https://api.useorbit.org ALLOW_PROD=i-understand \
  ACCESS_TOKEN=eyJ... HABIT_ID=<flexible-habit-guid> \
  k6 run load-tests/scenarios/mixed.js
```

Recommended order on the day: `habits-list` → `log-habit` → `chat` → `mixed`, starting at the default low rates. Raise `PEAK_RPS` / `READ_RPS` one step at a time across runs and stop as soon as p95/error-rate climbs.

## Environment variables

| Var | Default | Purpose |
| --- | --- | --- |
| `BASE_URL` | `http://localhost:8080` | Target API root. `api.useorbit.org` triggers the prod gate. |
| `ALLOW_PROD` | — | Must equal `i-understand` to run against prod. |
| `ACCESS_TOKEN` | — | Pre-obtained JWT (the prod path). Skips login. |
| `REFRESH_TOKEN` | — | Optional; stored but not used for load (refresh rotates). |
| `TEST_EMAIL` / `TEST_CODE` | — | One-time login on non-prod targets with a `TEST_ACCOUNTS` entry. |
| `HABIT_ID` | — | Required for `log-habit` and `mixed`. Use a dedicated flexible habit. |
| `LOG_DATE` | (today) | Override the logged date (must be within the last 7 days). |
| `CHAT_MESSAGE` | greeting | Override the chat message (keep it action-free). |
| `PEAK_RPS` | 5 (list) / 3 (log) | Per-scenario peak requests/sec. |
| `READ_RPS` / `LOG_RPS` / `CHAT_RPM` | 4 / 1 / 6–10 | `mixed` per-stage rates. |
| `ABORT_ERROR_RATE` | `0.10` | Kill-switch error-rate ceiling. |
| `ABORT_P95_MS` / `ABORT_P99_MS` | `1500` / `3000` | Kill-switch latency ceilings (chat: 8000 / 15000). |

## Reports

Each run prints a k6 summary and writes artifacts to `load-tests/results/` (git-ignored):

- `<scenario>.summary.json` — throughput (req/s + total), latency avg/p95/p99/max, error rate, 429 count, check pass/fail.
- `<scenario>.summary.md` — the same as a markdown table.

### Record the baseline

After the run, capture in the issue / runbook notes:

- The **highest sustained RPS** (and inferred concurrent users) where p95 stayed acceptable and error rate stayed ~0 → the "safe concurrent users" figure.
- p95 / p99 at that level, per endpoint.
- Where 429s started (chat is expected at >20/min/user) and any 5xx (the real failure signal).
- Bottleneck candidates to follow up with `/audit-performance`: slow `GET /api/habits` under load (N+1 / pagination), DB connection-pool saturation (Supabase session pooler), and chat latency dominated by the OpenAI call.

## Cleanup / teardown

- **Habit logs:** delete the dedicated `HABIT_ID` habit afterwards (it cascades its single log row), or unlog it once via the app. No other rows are created by the read or chat paths.
- **Gamification:** the test account's streak/XP reflects the logging done during the run; reset or discard the account if you want a clean slate.
- **Rate-limit buckets** (`DistributedRateLimitBuckets`) expire on their own and need no cleanup.
