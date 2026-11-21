# Proactive check-in provider probe

This opt-in console runner sends two fixed sample habits through the real
`AiProactiveCheckinMessageService` to the provider. The service builds the real system prompt and user prompt, calls the real
`BudgetedAiCompletionClient` and `AiCompletionClient`, and runs its own grounding check on the answer.
The probe proves that the model writes the `{{habit:N|phrase}}` grammar and that the grounding check accepts the answer.
The offline tests in `tests/Orbit.Infrastructure.Tests/Services/AiProactiveCheckinMessageServiceTests.cs` do not prove this.

The probe replaces only the daily budget ledger, the usage recorder and the logger. It starts no server and uses no database.
It is not part of `dotnet test`, and no CI job runs it.
A live run is a paid model call. Write the cost estimate on the ticket before a live run. The dry run gives that estimate.

## Usage

```sh
dotnet run -c Release --project bench/Orbit.ProactiveProbe -- --dry-run
ORBIT_PROACTIVE_PROBE_API_KEY=<eval key> dotnet run -c Release --project bench/Orbit.ProactiveProbe
```

- `ORBIT_PROACTIVE_PROBE_API_KEY` (required for a live run): the eval key.
- `ORBIT_PROACTIVE_PROBE_MODEL` (optional, default `gpt-6-luna`) and `ORBIT_PROACTIVE_PROBE_BASE_URL` (optional, default `https://api.openai.com/v1`).
- A live run makes 6 calls: 3 in pt-BR with trigger `MorningPlan`, and 3 in English with trigger `EveningCheckin`.
- The probe stops before a call that could take the total cost past 0.02 USD.
- The prices come from `AI:Pricing` in `src/Orbit.Api/appsettings.json` of this checkout. Run the probe inside the checkout.

## Output

- Each `PLAN` line gives the prompt tokens (`o200k_base`) and the estimate of one call: the prompt tokens at the cache-write price plus 512 output tokens at the output price.
- Each `CALL` line gives the HTTP status, the raw `text`, `habitTitles` and `goalTitles` of the answer, the grounding result
  (`grounded`, `refused` with the reason code, or `generation_failed`), the input and output tokens and the cost.
- The last `PROBE` line gives the totals.

## Result

- A pass: every `CALL` line has `http=200` and `result=grounded`, and the exit code is 0.
- A refusal is a finding, not a probe failure: the `CALL` line shows the `reason` code, and the exit code stays 0.
- Exit code 1: a call returned a status outside 200 to 299. Exit code 2: the key is missing.
