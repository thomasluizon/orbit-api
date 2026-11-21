# AI verification

Local verification is offline. Fake completion clients and captured provider responses in `tests/Orbit.Application.Tests/Fixtures/` exercise the production request builder, the typed results, the composition and the held previews. These tests make no OpenAI request.

Run the Release build, the affected xUnit projects and the OpenAPI freshness check:

```sh
dotnet build -c Release
dotnet test tests/Orbit.Domain.Tests -c Release
dotnet test tests/Orbit.Application.Tests -c Release
dotnet test tests/Orbit.Infrastructure.Tests -c Release
python3 .github/scripts/openapi_artifacts.py freshness
```

The API controller tests belong to `Orbit.Infrastructure.Tests`. There is no separate API test project. Contract generation does not start a listening application server.

## Request cache and cost evidence

`AiInputBudgetTests.Cache.cs` captures the real SDK request through a local HTTP transport. It checks that the system bytes and the tool bytes stay identical between requests and that the tool-loop input only grows at the end. `AiInputBudgetTests.LiveCache.cs` does the same for a four-turn sequence.

The cost figures in these tests are offline estimates from the `o200k_base` tokenizer. They are not provider usage. The tests read the Luna prices from `appsettings.json`. Do not replace the release measurement with an estimate. Do not send a paid warm-up request to make a test pass.

The OpenAI [prompt caching guide](https://developers.openai.com/api/docs/guides/prompt-caching) is the source for the routing, the matching and the cache-write rules. Keep the default cache lifetime of Luna. The ticket that orders a cost change decides a different lifetime.

## Live runs and paid probes

Before any live run or paid-key probe, write the cost estimate on the ticket that orders the run. The estimate lists:

- the model and its prices
- the case count and the request count
- the expected input, cached input, output and reasoning
- the retry allowance
- the maximum estimated cost

Confirm that the model comes from the Terraform and Render environment-group configuration of the service. A stale SSM model parameter is not that source.

Run each case once. Run each failed case one more time. Stop the live run when a rerun fails. Fix a failure with an offline replay of the captured response, which costs nothing.

## Evaluation key

- Use only the isolated evaluation project and `AI__EvalApiKey`. This rule also applies to a direct probe.
- Read the key from the SSM SecureString of the environment. Never print it.
- Never fall back to the production key.
- Confirm the project attribution and its independent monthly limit in the OpenAI Platform.
- The shared `AI__EvalDailyBudgetUsd` limit is $1.20 for each UTC day in staging and $0.50 for each UTC day in production.
- Stop the run when a response reports a quota error. Do not change the account, the key, the budget or the accounting date to continue.
- A direct probe must count the remaining daily allowance before it sends a request.

## Post-release measurement

After a release, send real turns. Read the `AI cost ... (partial)` line that the process logs at startup for the current UTC day, or read the next full daily line. Record these values on the ticket:

- the number of calls
- the dollar cost for each call
- the uncached input and the cached input for each call
- the output and the reasoning

The cache hit fraction is `cached_input / (cached_input + uncached_input)`. The output already includes the reasoning.

The partial line includes earlier calls of the same UTC day. Use counter deltas, or use a full day that ran only on the released version, to find the cost of one change. Record the total cost and the failures of a live run on the ticket. Before you start or restart a production process for a cost snapshot, check for an active live run or release.
