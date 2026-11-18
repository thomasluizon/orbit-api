# Change list provider probe

Tracks thomasluizon/orbit-tickets#2160.

This opt-in console runner builds the real tool declarations of the first chat request on the `ai_change_plan` path.
It builds them for each declaration shape (`wrapper`, `kinds`) and each client catalog (`legacy`, `composition`, `offers`).
A live run sends each catalog to the provider one time. It proves that the provider accepts the schema depth and the schema size.
The offline tests in `tests/Orbit.Infrastructure.Tests/AI/ChangeListCatalogTests.cs` read the same catalog builder. They do not prove provider acceptance.

The runner does not start a server and does not use a database. It is not a part of `dotnet test`, and no CI job runs it.
A live run is a paid model call. Write the cost estimate on the ticket before a live run. The dry run gives that estimate.

## Usage

```sh
dotnet run -c Release --project bench/Orbit.ChangeListProbe -- --dry-run
ORBIT_CHANGE_PROBE_API_KEY=<eval key> dotnet run -c Release --project bench/Orbit.ChangeListProbe
dotnet run -c Release --project bench/Orbit.ChangeListProbe -- --shape wrapper
```

- `ORBIT_CHANGE_PROBE_API_KEY` (required for a live run): the eval key. The probe never prints it.
- `ORBIT_CHANGE_PROBE_MODEL` (optional, default `gpt-6-luna`) and `ORBIT_CHANGE_PROBE_BASE_URL` (optional, default `https://api.openai.com/v1`).
- `--shape wrapper|kinds|both` (default `both`) and `--dry-run` (no call).
- A live run makes 6 calls at most: one for each shape and client catalog, with one user message ("Reply with the single word OK. Call no tool."), effort low and 128 output tokens at most.
- The prices come from `AI:Pricing` in `src/Orbit.Api/appsettings.json` of this checkout. Run the probe inside the checkout.

## Output

- Each `CATALOG` line gives the tool count, the declaration tokens (`o200k_base`), the schema string size, the depth and the estimate.
- Each `CALL` line gives the HTTP status, `result=pass` or `result=fail`, the provider error text on a failure, the input and output tokens and the cost.
- The last `PROBE` line gives the totals.

The estimate of one call is the declaration tokens at the cache-write price plus 128 output tokens at the output price.

## Result

- A pass: every `CALL` line has `http=200 result=pass`, and the exit code is 0.
- A fail: a `CALL` line has `result=fail` with the HTTP status (for example 400) and the provider error text, and the exit code is 1.
- Exit code 2: the key is missing, or `--shape` has a wrong value.
