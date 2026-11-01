# Astra schema and cost evidence

The baseline is the inlined catalog from `8e6e3690`, exported through the real SDK
request builder with a local HTTP transport. It contains the unnormalized source
schema for every registered chat tool and the normalized provider schemas for legacy,
composition and offer clients. No request reaches OpenAI. The snapshot contains schemas
only, without account data, API credentials or provider response text.

The bounded schema builders remain the execution contract. Export compaction recognizes
the repeated habit structure, keeps a single recursive habit definition, and shares
identical schema blocks within a tool when a reference saves space. The strict normalizer
handles optional references as nullable unions. The operation catalog exports the same
compact representation. `update_habit`, `bulk_update_habits` and
`bulk_reschedule_habits` have no repeated parameter block large enough to save space;
their individual parameter fields and descriptions remain intact. Their long tool
instructions are not repeated parameter subschemas.

Every reference is expanded in the equivalence test. Recursion is projected at the
existing server boundary: five habit levels including the root, or four remaining
levels for `create_sub_habit`. That projection removes the two fields absent from the
old leaf (`parent_habit_id` and `sub_habits`), their required entries when present, and
the corresponding two eligible offer fields. Nullable reference unions are normalized
back to the equivalent type union, and only the new depth sentence is removed before
comparison. All other constraints and descriptions must compare equal. The recursive
provider schema admits deeper shapes; the unchanged bounded server check rejects them.
The command handlers also retain the configured depth check against existing parents.

## Regenerate the inlined fixture

Run this from the repository root. The exporter is independent of the new compactor,
so it can be copied into the pinned source tree. The temporary tree is an offline
build checkout and does not start a server.

```sh
schema_workspace=$(mktemp -d)
git archive 8e6e3690 | tar -x -C "$schema_workspace"
cp tests/Orbit.Infrastructure.Tests/AI/AiInputBudgetTests.SchemaExport.cs "$schema_workspace/tests/Orbit.Infrastructure.Tests/AI/AiInputBudgetTests.SchemaExport.cs"
dotnet test "$schema_workspace/tests/Orbit.Infrastructure.Tests" -c Release --filter 'FullyQualifiedName~SchemaSnapshots_ExportCurrentContracts'
cp "${TMPDIR:-/tmp/}orbit-1916-tool-schemas.json.gz" tests/Orbit.Infrastructure.Tests/Fixtures/ToolSchemas/inlined.json.gz
```

The same exporter on the current source writes the compact schemas to the temporary
file. Never replace the baseline with that output: it would make the comparison
self-referential. Gzip container bytes can differ; compare the decompressed JSON.

## Regression evidence

Before changing implementation or tests, the unchanged cache-cost and strict-schema
checks passed all 33 cases, exit 0:

```sh
dotnet test tests/Orbit.Infrastructure.Tests -c Release --filter 'FullyQualifiedName~AiInputBudgetTests.CacheRequests_RepresentativeWarmPrefixMeanCostsAtMostNineTenThousandths|FullyQualifiedName~StrictChatToolSchemaTests' --logger 'console;verbosity=quiet'
```

With only `StrictToolSchema.cs` restored to `8e6e3690` and the final cold-budget
assertions in place, the strengthened test failed all three clients, exit 1:

```sh
dotnet test tests/Orbit.Infrastructure.Tests -c Release --filter 'FullyQualifiedName~CacheRequests_RepresentativeWarmPrefixMeanCostsAtMostNineTenThousandths' --logger 'console;verbosity=normal'
```

The failures were first cold calls of $0.007165625 against $0.0054 (legacy),
$0.007879875 against $0.0061 (composition), and $0.008585125 against $0.0068 (offers).
Restoring the compactor made all three pass. The focused cache/schema suite passed
57 cases, exit 0; the application reference, trace-redaction and configured-depth suite
passed 13 cases, exit 0. `RecursiveToolCall_OverServerDepthReturnsValidationFeedbackToModel`
replays a six-level provider tool call, runs the real tool argument check, and verifies
that the next SDK request returns `success: false` and `Invalid tool arguments.` to the
model, with no mediator call. Application checks accept five levels and reject six.

The first broad run exposed the resolver's handling of an absent preview schema:
the unchanged `PendingOperationChangePreviewerTests.HabitPreview_UsesStoredEmoji`
and other preview cases threw `InvalidOperationException`. The resolver now returns
non-object schemas unchanged, preserving the former undeclared-field result. The
entire application project passes after that fix: 9,855 tests, exit 0, using
`dotnet test tests/Orbit.Application.Tests -c Release`.

Existing SDK and catalog assertions also consumed inlined schemas directly. They now
expand references before checking payload order, ask constraints and catalog contents;
control-order and strictness checks still inspect the real referenced wire schema.
The schema-depth walker follows references and stops at an active recursion edge.
The affected infrastructure checks pass 253 cases, exit 0:

```sh
dotnet test tests/Orbit.Infrastructure.Tests -c Release --filter 'FullyQualifiedName~AiInputBudgetTests|FullyQualifiedName~StrictChatToolSchemaTests|FullyQualifiedName~ProviderToolControlOrderTests|FullyQualifiedName~ContinueWithToolResultsAsync_KeepsCatalogAndAppendsTypedAskConstraints|FullyQualifiedName~AgentCatalogServiceTests|FullyQualifiedName~AgentChatWriteHoldTests|FullyQualifiedName~AccountChangeSetRegressionTests' --logger 'console;verbosity=quiet'
```

The trace collector already traverses every definition exactly once without following
references. A recursive-definition test verifies nested sensitive fields are removed
and visible fields and execution arguments are retained. The argument validator and
optional-null walker resolve local references using the owning root schema. The
OpenAPI documents expose the catalog schema as JSON and need no new DTO fields.

## External interface evidence

The [official structured outputs guide](https://developers.openai.com/api/docs/guides/structured-outputs#supported-schemas)
confirms local definitions and recursive references, strict object requirements and
schema limits. The [official prompt caching guide](https://developers.openai.com/api/docs/guides/prompt-caching)
provides cache-write and lifetime behavior. The implementation introduces no SDK or
HTTP response field. The installed SDK receives schema bytes through its existing
function-tool constructor; the fake transport captures its actual serialized request
and all generated provider schemas pass the existing strict-schema invariant. This
proves SDK serialization, while provider acceptance remains the coordinator's probe.

## Assumptions

- Export compaction keeps the existing bounded builders as server validation contracts, rather than converting internal validation to an unbounded recursive schema. This preserves the requested validation feedback and keeps schema-only changes out of shared chat routing files.
- Equivalence means reference expansion within the domain's enforced depth, rather than unrestricted expansion of an intentionally recursive provider schema. The leaf projection and nullable canonicalization are explicit above.

## Offline costs

These are estimates using the installed `o200k_base` tokenizer and the asserted Luna
prices in `appsettings.json`, with captured output usage for reads and estimated
argument tokens for the structured write. Warm means another account already sent the
same static prefix. The cold mean includes one initially empty cache across seven
representative calls; it is not a mean of seven isolated cold requests. The first-cold
column gives the actual estimate for a new session. No paid call was made.

| Client | Tools before | Tools after | Warm mean before USD | Warm mean after USD | Cold mean before USD | Cold mean after USD | First cold before USD | First cold after USD |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| legacy | 45636 | 30656 | 0.0007673 | 0.0006171 | 0.0016884 | 0.0012921 | 0.0071656 | 0.0052931 |
| composition | 50099 | 34855 | 0.0008270 | 0.0006753 | 0.0018406 | 0.0014386 | 0.0078799 | 0.0059744 |
| offers | 54949 | 39302 | 0.0008930 | 0.0007351 | 0.0019970 | 0.0015826 | 0.0085851 | 0.0066293 |

The warm ceiling stays $0.0009. First-cold ceilings are $0.0054, $0.0061 and $0.0068;
means including a cold start are $0.00132, $0.00147 and $0.00162 respectively.
The released daily AI cost line determines ticket acceptance; these estimates cannot
replace production usage. The coordinator owns catalog regeneration in the mobile
repository, the real-model probe and staging measurement.

## Per-section token tables

The tests write these tables to `orbit-1916-cache-<client>.md` in the temporary directory.
They include prefix byte/token diagnostics as well; the columns below retain every
input section and both cost estimates. `Other` is zero in each row.

| Client/call | System | Tools | Context | History | User | Calls | Results | Offers | Cached | Warm USD | Cold USD |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| legacy/read/1 | 10488 | 30656 | 874 | 0 | 31 | 0 | 0 | 0 | 41088 | 0.0005680 | 0.0052931 |
| legacy/write/1 | 10488 | 30656 | 874 | 0 | 69 | 0 | 0 | 0 | 41984 | 0.0007957 | 0.0007957 |
| legacy/loop/1 | 10488 | 30656 | 874 | 0 | 69 | 0 | 0 | 0 | 41984 | 0.0004697 | 0.0004697 |
| legacy/loop/2 | 10488 | 30656 | 874 | 0 | 69 | 138 | 75 | 0 | 41984 | 0.0008223 | 0.0008223 |
| legacy/loop/3 | 10488 | 30656 | 874 | 0 | 69 | 1017 | 130 | 0 | 42240 | 0.0005477 | 0.0005477 |
| legacy/followup/1 | 10488 | 30656 | 874 | 188 | 31 | 0 | 0 | 0 | 41088 | 0.0005915 | 0.0005915 |
| legacy/other-user/1 | 10488 | 30656 | 879 | 0 | 31 | 0 | 0 | 0 | 41472 | 0.0005245 | 0.0005245 |
| composition/read/1 | 11718 | 34855 | 895 | 0 | 31 | 0 | 0 | 0 | 46464 | 0.0006310 | 0.0059744 |
| composition/write/1 | 11718 | 34855 | 895 | 0 | 69 | 0 | 0 | 0 | 47360 | 0.0008587 | 0.0008587 |
| composition/loop/1 | 11718 | 34855 | 895 | 0 | 69 | 0 | 0 | 0 | 47488 | 0.0005180 | 0.0005180 |
| composition/loop/2 | 11718 | 34855 | 1056 | 0 | 69 | 138 | 75 | 0 | 47488 | 0.0008908 | 0.0008908 |
| composition/loop/3 | 11718 | 34855 | 1056 | 0 | 69 | 1017 | 130 | 0 | 47872 | 0.0006013 | 0.0006013 |
| composition/followup/1 | 11718 | 34855 | 895 | 188 | 31 | 0 | 0 | 0 | 46592 | 0.0006398 | 0.0006398 |
| composition/other-user/1 | 11718 | 34855 | 900 | 0 | 31 | 0 | 0 | 0 | 46848 | 0.0005875 | 0.0005875 |
| offers/read/1 | 12305 | 39302 | 905 | 0 | 31 | 0 | 0 | 195 | 51584 | 0.0006971 | 0.0066293 |
| offers/write/1 | 12305 | 39302 | 905 | 0 | 69 | 0 | 0 | 195 | 52608 | 0.0009101 | 0.0009101 |
| offers/loop/1 | 12305 | 39302 | 905 | 0 | 69 | 0 | 0 | 195 | 52736 | 0.0005694 | 0.0005694 |
| offers/loop/2 | 12305 | 39302 | 1066 | 0 | 69 | 138 | 75 | 195 | 52736 | 0.0009421 | 0.0009421 |
| offers/loop/3 | 12305 | 39302 | 1066 | 0 | 69 | 1017 | 130 | 195 | 53120 | 0.0006527 | 0.0006527 |
| offers/followup/1 | 12305 | 39302 | 905 | 188 | 31 | 0 | 0 | 195 | 51584 | 0.0007206 | 0.0007206 |
| offers/other-user/1 | 12305 | 39302 | 910 | 0 | 31 | 0 | 0 | 197 | 51968 | 0.0006538 | 0.0006538 |

## Tool JSON characters

Canonical full provider tool JSON (name, description, strict flag and parameters),
counted as UTF-16 characters with HTML escaping disabled. Every tool above 3,000
characters before or after compaction is listed for every negotiated client. This
snapshot measures this API branch's SDK requests; a separately generated mobile eval
catalog can use different wrapping and escaping. The report test writes the same table
to `orbit-1916-catalog-sizes.md` in the temporary directory.

| Client | Tool | Before JSON characters | After JSON characters |
| --- | --- | ---: | ---: |
| legacy | bulk_create_habits | 36648 | 8778 |
| legacy | bulk_reschedule_habits | 4560 | 4560 |
| legacy | bulk_update_habits | 16704 | 16704 |
| legacy | create_habit | 35999 | 12076 |
| legacy | create_sub_habit | 28475 | 11659 |
| legacy | query_habits | 3622 | 3622 |
| legacy | suggest_breakdown | 4044 | 4044 |
| legacy | update_habit | 17680 | 17680 |
| composition | ask | 15635 | 13920 |
| composition | bulk_create_habits | 36648 | 8778 |
| composition | bulk_reschedule_habits | 4560 | 4560 |
| composition | bulk_update_habits | 16704 | 16704 |
| composition | create_habit | 35999 | 12076 |
| composition | create_sub_habit | 28475 | 11659 |
| composition | get_period_analytics | 3037 | 2721 |
| composition | query_habits | 3877 | 3877 |
| composition | suggest_breakdown | 4044 | 4044 |
| composition | update_habit | 17680 | 17680 |
| offers | ask | 15872 | 14157 |
| offers | bulk_create_habits | 37960 | 9230 |
| offers | bulk_reschedule_habits | 4797 | 4797 |
| offers | bulk_update_habit_emojis | 3226 | 3226 |
| offers | bulk_update_habits | 17156 | 17156 |
| offers | create_habit | 37311 | 12679 |
| offers | create_sub_habit | 29572 | 12262 |
| offers | get_period_analytics | 3274 | 2958 |
| offers | query_habits | 4114 | 4114 |
| offers | suggest_breakdown | 4281 | 4281 |
| offers | update_habit | 18132 | 18132 |

## Copy for coordinator approval

No user-visible strings change in this round. Tool descriptions use English across
account languages; Portuguese below is the corresponding review translation.
All other schema descriptions are retained and pinned by the equivalence fixture.

| Location | en | pt-BR |
| --- | --- | --- |
| `sub_habits.description`, original text plus the new depth sentence | Independently trackable activities under this parent. Follow the destination structure and account pattern. An empty account's new routine defaults to this form. Preserve each requested time. Maximum 5 habit levels including the root; the server validates nesting depth. | Atividades acompanhadas de forma independente sob este hábito principal. Siga a estrutura de destino e o padrão da conta. Em uma conta vazia, uma nova rotina usa esse formato por padrão. Preserve cada horário solicitado. No máximo 5 níveis de hábitos, incluindo a raiz; o servidor valida a profundidade. |
| `ProactiveProposal` field description retained from this branch | Names of unrequested configured fields, following the offer instructions in the system prompt. | Nomes dos campos configurados sem solicitação, seguindo as instruções de sugestões no prompt do sistema. |
| `HabitModelInstructions.TitleLanguage`, retained template | Write composed habit titles in {language}. | Escreva os títulos de hábitos no idioma {language}. |

The title template's actual model-facing values are `Write composed habit titles in English.`
for en and `Write composed habit titles in Brazilian Portuguese.` for pt-BR. The existing
history wrapper and instructions moved into the system prompt keep their text unchanged.
No pattern over a person's message selects tools, reroutes a tool call, triggers an
uncalled tool or replaces an answer in this branch. Tool selection still depends on
negotiated capabilities and the registry; compaction reads only schema structure.
