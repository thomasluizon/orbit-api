# Suggestion count and issued choice captures

The `g11-brain-dump-ideas-pt-BR-r1`, `r2` and `r3` projections come from
the coordinator's `eval-gate-prod-r1/evals/astra/reports/attempts` output,
production API `9ba7dfea`, harness `6cae53206`. They retain `caseId`, `repeat`,
`pass`, `reason`, every tool name, argument and reported status, and the
`groundingEvidence.successfulTools` projections. No credentials are included.

Repeats 1 and 3 request `count: 1` and hold all three proposed items in one
`bulk_create_habits` preview. Repeat 2 requests `count: null`, receives three
drafts and issues a successful `ask` pick list. Its captured answer incorrectly
shows only `habitSuggestions`, hiding that issued selection.

`G11BrainDumpReplayTests` replays the calls at `IAiIntentService`. The successful
preview cases use the real query and suggestion paths with the captured draft
generator output. Redacted draft titles, dates and times are recovered only from
the same capture's unredacted bulk arguments. Missing draft IDs and account IDs
are assigned locally; seeded account habits are eligible on the replay date.

The repeat 2 composition replay supplies its historical successful suggestion
result at `IAgentOperationExecutor`. This keeps the original null-count call and
three-draft result unchanged after new calls begin rejecting null counts. The
real `AskTool` validates and issues the captured selection. The test requires
that selection to reach the answer, with no manufactured write or preview.
Separate tool and serialized SDK-schema tests cover the explicit bounded count.

These replays verify structured results and composition, not model reliability.
The coordinator owns the production pass^3 rerun.
