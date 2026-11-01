# Astra verification

Local verification is offline. Fake completion clients and captured provider responses in
`tests/Orbit.Application.Tests/Fixtures/` exercise the production request builder, typed
results, composition and held previews without an OpenAI request.

Run the Release build, the affected xUnit projects and the OpenAPI freshness check:

```sh
dotnet build -c Release
dotnet test tests/Orbit.Domain.Tests -c Release
dotnet test tests/Orbit.Application.Tests -c Release
dotnet test tests/Orbit.Infrastructure.Tests -c Release
python3 .github/scripts/openapi_artifacts.py freshness
```

The API controller tests belong to `Orbit.Infrastructure.Tests`; there is no separate API
test project. Contract generation does not start a listening application server.

## Request cache and cost evidence

`AiInputBudgetTests.Cache.cs` captures the real SDK request through a local HTTP transport.
It replays a recorded terminal read and structured bulk-create output, and checks legacy,
composition and offer clients. Each includes a read, a held write, a read/write loop,
a follow-up with client history and another user's first request. The template-edit replay
also drives the real handler through a read, a held preview and a continuation.

The tests compare UTF-8 system and tool bytes, verify append-only tool-loop input and
preserve bounded, individually quoted history entries. Old client history beyond the
existing count and character limits still expires. The client supplies text history, not
previous tool-loop response items, so this does not restore unavailable tool history.

Cost tables use the installed `o200k_base` tokenizer over canonical serialized tool and
input sections, without HTML escaping. They are offline estimates, not provider usage.
The configured Luna prices are asserted against `appsettings.json`: $0.10 input,
$0.01 cached input, $0.125 cache writes and $0.50 output per million tokens. Read output
usage comes from the captured response; the structured write's argument tokens are
estimated because its trace has no provider usage envelope.

The D147 comparison assumes another account has already sent the same static prefix.
Only an observed matching prefix of at least 1,024 tokens qualifies for reuse, rounded
conservatively down to 128 tokens; every new input token uses the cache-write rate.
The test asserts a mean at most $0.0009 for each client type. It also reports every cold
call and the mean including a cold start. Cold requests exceed the warmed D147 target;
that target is not a guarantee for cold requests, arbitrary account sizes, cache eviction,
provider routing, or a maximum-size output. Do not replace the release measurement with
this estimate or send paid warm-up requests to make it pass.

The source for routing, matching and cache-write behavior is the
[official OpenAI prompt caching guide](https://developers.openai.com/api/docs/guides/prompt-caching).
Luna uses automatic cache routing. The existing shared `prompt_cache_key` is retained;
no observed production routing-overflow evidence justifies splitting its cache.

## Launch gate

The coordinator runs the live gate only after the cost change is released, when the app
launches. Run each case once, then rerun each failing case once. A failed rerun stops the
live gate. Fix rounds replay the captured real responses offline at zero provider cost;
they do not restart the gate or repeat passing cases.

Before any live gate or paid-key probe, put its written cost estimate on
`thomasluizon/orbit-tickets#1916`: model, prices, case and request counts, expected input,
cached input, output and reasoning, retry allowance and maximum estimated cost. Confirm
the selected model comes from the same Terraform/Render environment-group configuration
as the service. A stale SSM model parameter is not that source.

Use only the isolated evaluation project and `AI__EvalApiKey`, including direct probes.
The key comes from the environment's SSM SecureString and must never be printed. Do not
fall back to the production key. Confirm project attribution and its independent monthly
limit in OpenAI Platform. The shared `AI__EvalDailyBudgetUsd` ceiling defaults to $0.50
per UTC day in each environment. A quota response stops the run; do not change accounts,
keys, the budget or the accounting date to continue. A direct probe must account for the
same remaining daily allowance before dispatch; the project limit also remains enforced.

After release, use real turns and inspect the current UTC day's `AI cost ... (partial)`
line emitted at startup, or the next full daily line. Record calls, dollar cost per call,
uncached and cached input per call, output and reasoning on the ticket. Cache hit fraction
is `cached_input / (cached_input + uncached_input)`; reasoning is already part of output.
The coordinator records the gate's actual total cost and failures on the ticket. Starting
or restarting a production process for a cost snapshot requires coordination with any
active gate or other release work.

The repository's [astra-eval skill](.claude/skills/astra-eval/SKILL.md) uses this procedure.
