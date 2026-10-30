# Production restart replays

The `prod-r2-` fixtures project the model tool calls and original preview titles
and due dates from production gate rerun r2, API `ac4bd7c8`, harness `2b66459a8`.
The first two attempts queried the current set and completed their preview. The
third skipped the query and declared its write incomplete.

Calls retain their captured arguments. At the model-service port, tests bind the
redacted IDs to domain-created habits and append `restart_plan=true` for the new
schema. Completion variants set `orbit_write_complete=false`. Refusal tests then
replay a captured query and corrected write, asserting no preview was staged by
the rejected call. The account also contains a completed task that must stay out
of the restart set. These replays verify server behavior; they do not measure
live-model reliability. The coordinator owns the production pass^3 rerun.

| Source attempt | SHA-256 |
| --- | --- |
| `composition-restart-approve-pt-BR-r1.json` | `250d46ab054718ffe017218127cb4dce4b86e9da5c40b4e5934409145e2605a0` |
| `composition-restart-approve-pt-BR-r2.json` | `d974a56280bbd8f211a264e304510055bf9ee928839f77d7e94ef76153277559` |
| `composition-restart-approve-pt-BR-r3.json` | `fb1702198c3c00e1c1ecb47bdea32581c4a3a0d6db94ea04be0b77f797bdd628` |
