---
name: astra-eval
description: Verify Astra through offline provider-response replays, or run the coordinator-authorized launch gate after the cost change is released.
---

# Astra evaluation

Read [TESTING.md](../../../TESTING.md) for the request-cost model, verification commands
and launch-gate procedure. Ordinary fixes use captured responses offline at zero provider
cost. A live run requires the coordinator's launch authorization and a written cost
estimate on the owning ticket; this skill does not authorize one by itself.

At launch, run each case once and rerun each failing case once. Stop after a failed rerun
or a quota response. Do not repeat passing cases or restart the live gate during fixes.
Replay the captured real provider responses offline instead.

Evaluation and probes use the isolated evaluation project and `AI__EvalApiKey`, with the
shared $0.50 UTC daily ceiling and independent project monthly limit. Never use the
production key, print a credential, raise a budget or rotate accounts to continue a run.
No paid-key probe runs without a written cost estimate and remaining allowance.

Record the launch gate's actual cost, the daily or partial `AI cost` line, cache fraction
and per-purpose token averages on `thomasluizon/orbit-tickets#1916`. The offline warmed
prefix estimate does not establish the released service's measured cost.
