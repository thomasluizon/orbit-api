# Brain dump model replays

Tracks thomasluizon/orbit-tickets#1856. These authored en and pt-BR fixtures replay
structured output at `IAiIntentService`, using the repository's `AiToolCall` contract
and registered `bulk_create_habits` argument schema. They are deterministic unit
fixtures, not captured provider responses or real-call evaluation evidence.

The chat handler tests substitute account IDs and local dates, then assert one
pending preview, task/habit grouping metadata, existing-habit reuse, no writes,
narrative suppression and stored response replay. Infrastructure tests consume
the same fixtures through the real operation gateway and application commands.
They cover approval, an edited task becoming recurring, rejection and independent
persisted readback through `GetHabitByIdQuery`.

The coordinator owns the paired web/Android renderer ticket and real-call cases
in the consumer's `evals/astra`. Those cases must pass three consecutive runs in
each language, including existing-habit reuse and persisted readback. Local
replays do not establish that the live model chooses the right tool plan.
