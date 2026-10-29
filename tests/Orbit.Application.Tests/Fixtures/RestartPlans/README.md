# Restart plan replays

The authored `en.json` and `pt-BR.json` fixtures replay structured `IAiIntentService` output, using the
repository's `AiToolCall` and tool schemas. They are not provider captures or
real-call eval results. The test binds habit IDs from its domain-created records.
The English and native pt-BR requests exercise the same command path without
message-based routing.

`RestartPlan_ModelPortReplay_PreviewsThenAppliesOnlyApprovedDates` runs the chat
handler, existing reads, pending store, previewer and real bulk update command.
It checks approval, date editing and rejection against fresh persisted readback
from the unit-test EF InMemory store. The coordinator owns real-call pass^3
cases in the paired consumer's `evals/astra`.

The `g9a-` fixtures contain captured model calls. [GateNine.md](GateNine.md)
records their provenance and replay limits. The captured replays cover six-habit
plans, exact counts, approval readback and a replacement preview after cancellation.
