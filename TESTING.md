# Astra verification

## Launch gate

The coordinator runs the paid live gate once for the app launch. Each failing case may
be rerun once. A fix round replays captured real provider responses offline using the
existing model-service port or a fake completion client. It makes no OpenAI request.
Do not run a paid probe without a written cost estimate on thomasluizon/orbit-tickets#1916.

Before the launch gate, confirm the `Orbit Eval` project and its key in OpenAI Platform,
its owner-selected monthly budget, `AI__EvalApiKey` in the two API environment groups,
and `StagingEval__AccountIds` containing every gate account. The hard API ceiling is
`AI__EvalDailyBudgetUsd=0.50` for all evaluation accounts together per environment and
UTC day. Unsettled reservations consume that ceiling. A quota error stops the entire
gate. Do not resume it on another account, environment, key or day to bypass the stop.
Direct probe scripts must share the coordinator's aggregate spend ledger and stop rule.

Write the estimated maximum cost before dispatch and the actual gate cost afterward on
the owning ticket. Count every model round, permitted rerun and direct probe. The
coordinator owns the probe scripts and the separate `astra-eval` skill update.

After release, use each purpose's daily `AI cost` line to record cached input, uncached
input, output, reasoning, calls, cost per call and cache hit fraction. Reasoning is a
subset of output. The ordinary chat acceptance target is at most $0.0009 per call.
This is release evidence; offline tests cannot establish the production cache hit rate.

## Prefix cost comparison

The coordinator measured 56,654 raw-request tokens for the per-client catalog and
83,339 for the uniform catalog. At a 46.4% cache hit rate and D147's input prices
($0.10 uncached and $0.01 cached per million), the blended rate is $0.05824 per million.

| Catalog | Input tokens | Input cost per call | With 500 output tokens at $0.50 per million |
| --- | ---: | ---: | ---: |
| Per-client | 56,654 | $0.00329953 | $0.00354953 |
| Uniform | 83,339 | $0.00485366 | $0.00510366 |

The uniform input costs 47.1% more at that hit rate, an extra $0.00155413 per call.
The implementation therefore keeps schemas stable within a client capability set.
The typed read or missing-field result adds `ask` once; it never exposes ask before
its prerequisite. No request wording, language or habit-title pattern changes the catalog.
Shared-title guidance appears once in dynamic context instead of every write description.

`ProviderToolControlOrderTests.ChatRequests_ShareStaticPrefixAcrossUsersOffersPreviewsAndHistory`
compares UTF-8 bytes across different users, languages, history and pending previews
within both capability sets. It measures the serialized system, tools and raw request
with the existing `o200k_base` tokenizer. These are offline estimates, not provider bills.
[Official OpenAI documentation](https://developers.openai.com/api/docs/guides/prompt-caching)
explains why prefix stability helps caching without guaranteeing a cache hit.

## Output evidence

The captured medical bulk-create arguments measure 1,189 `o200k_base` tokens. Chat's
4,096-token cap leaves 2,907 tokens for reasoning and framing. Daily summaries use 600.
Check-ins and slip alerts still use 512: the repository has no real provider outputs
for those two services, so a measured replacement cap cannot yet be justified. Proactive
chat/offer fixtures and hand-authored notification unit-test responses are different
evidence. Supply the service captures, including output and reasoning usage, before
claiming this part of #1916 complete. Truncation warnings identify the affected purpose.

## External-interface evidence

Response usage fields remain backed by the installed OpenAI 2.14.0 serializer and the
captured `Fixtures/AiReservation/8.json.gz` usage, exercised by `AiEvalIsolationTests`.
No live provider probe is part of verification.

Evaluation audio reads local media metadata through TagLibSharp 2.3.0. Its NuGet package
has no runtime dependencies. The matching producer sources confirm `File.Create`,
`IFileAbstraction`, `ReadStyle.Average`, and `Properties.Duration`:
[File.cs](https://github.com/mono/taglib-sharp/blob/TaglibSharp-2.3.0.0/src/TaglibSharp/File.cs),
[Properties.cs](https://github.com/mono/taglib-sharp/blob/TaglibSharp-2.3.0.0/src/TaglibSharp/Properties.cs).
Unknown or zero duration prevents dispatch. Pricing follows the
[official Whisper model page](https://developers.openai.com/api/docs/models/whisper-1).
The service reads no new provider response field to settle audio reservations.

## Copy for coordinator approval

These model-facing strings from this branch remain English at runtime for both locales.
The pt-BR column is a review translation. Existing user-visible quota copy is also used
for transcription quota errors; the mobile consumer retains its existing code mapping.
The two former capability-unavailable prompt strings are removed with the uniform schema.

| Source | en | pt-BR |
| --- | --- | --- |
| Ask draft binding | Copy the typed missing-field result's draft_call_id when asking about a new habit draft; otherwise null. | Copie o draft_call_id do resultado tipado de campo ausente ao perguntar sobre um novo hábito em rascunho; caso contrário, use null. |
| Ask missing field | Copy that draft's missing_field exactly and ask only for that field; otherwise null. | Copie exatamente o missing_field desse rascunho e pergunte apenas sobre esse campo; caso contrário, use null. |
| Ask constraints | Apply these ask constraints for this round. Use only the listed forms and missing_input values. For a bound draft, copy draft_call_id and missing_field exactly and ask only for that field with one question. These constraints do not authorize writes. | Aplique estas restrições de ask nesta rodada. Use apenas os formulários e valores de missing_input listados. Para um rascunho vinculado, copie exatamente draft_call_id e missing_field e faça uma única pergunta apenas sobre esse campo. Estas restrições não autorizam alterações. |
| Existing quota error | Your daily AI safety budget has been reached. Please try again tomorrow. | Seu limite diário de segurança de IA foi atingido. Tente novamente amanhã. |

The existing shared-title instruction is moved without changing its wording:

| en | pt-BR |
| --- | --- |
| For an existing habit target with sharesTitle=true in habitTargets, read query_habits with for_selection=true and wait for the person's choice before calling any write tool. Never emit the write alongside that read or use every matching ID for a singular target. An explicitly requested full set uses a fresh unfiltered read with for_selection=false. | Para um hábito existente com sharesTitle=true em habitTargets, consulte query_habits com for_selection=true e aguarde a escolha da pessoa antes de chamar uma ferramenta de alteração. Nunca emita a alteração junto com essa consulta nem use todos os IDs correspondentes para um alvo singular. Um conjunto completo pedido explicitamente usa uma nova consulta sem filtro com for_selection=false. |

## Repository checks

Run by exit code:

```sh
dotnet build -c Release
dotnet test tests/Orbit.Domain.Tests
dotnet test tests/Orbit.Application.Tests
dotnet test tests/Orbit.Infrastructure.Tests
python3 .github/scripts/openapi_artifacts.py freshness
```

No server, browser, live eval or additional suite is required for this change.

## Regression evidence

The unchanged tests on `66051aa4` passed with the defects present:

```sh
dotnet test tests/Orbit.Infrastructure.Tests --filter 'FullyQualifiedName~AiDailyBudgetTests|FullyQualifiedName~ProviderToolControlOrderTests|FullyQualifiedName~AiOutputTokenLimitTests' --logger 'console;verbosity=minimal'
dotnet test tests/Orbit.Application.Tests --filter 'FullyQualifiedName~ProactiveWriteReplay|FullyQualifiedName~Ask_EmitsNegotiatedPartAndIgnoresRepeatedAsk' --logger 'console;verbosity=minimal'
dotnet test tests/Orbit.Infrastructure.Tests --no-build --filter 'FullyQualifiedName~ContinueWithToolResultsAsync_KeepsCatalogAndAppendsTypedAskConstraints|FullyQualifiedName~AuthenticatedRequests_SelectProjectBeforeAnyBudgetContext' --logger 'console;verbosity=minimal'
dotnet test tests/Orbit.Infrastructure.Tests --filter 'FullyQualifiedName~AiEvalIsolationTests' --logger 'console;verbosity=minimal'
```

These commands exited zero with 100, 18, 8 and 13 passing cases respectively.

Strengthened tests failed before their corresponding fixes:

```sh
dotnet test tests/Orbit.Infrastructure.Tests --filter 'FullyQualifiedName~EvalCeiling_IsShared|FullyQualifiedName~ContinueWithToolResultsAsync_KeepsCatalogAndAppendsTypedAskConstraints' --logger 'console;verbosity=minimal'
dotnet test tests/Orbit.Application.Tests --filter 'FullyQualifiedName~UnsupportedClients_EmptySuggestionPlaceholders' --logger 'console;verbosity=minimal'
dotnet test tests/Orbit.Infrastructure.Tests --filter 'FullyQualifiedName~EvalAudio_ReservationFailure' --logger 'console;verbosity=minimal'
dotnet test tests/Orbit.Infrastructure.Tests --filter 'FullyQualifiedName~Calls_SelectCredentialByAccountForEveryCompletionPath|FullyQualifiedName~AuthenticatedRequests_SelectProjectBeforeAnyBudgetContext' --logger 'console;verbosity=minimal'
```

All four commands exited one. The shared-ceiling/constraint group had three intended
failures: the second account was admitted above the ceiling, and both buffered and
streamed third rounds contained two copies of the constraint. Empty-placeholder cases
had three failures because the normalized arguments still selected offer execution.
Both audio cases failed because reservation errors did not prevent provider dispatch
or retain the quota code. Credential tests had six failures and twelve passes because
missing evaluation credentials still allowed production-key dispatch.

The stronger chat-execution and truncation tests also ran with the affected source
temporarily restored from `66051aa4`, then restored to the fix. These observations are
reproductions of the defect, rather than claims that the tests were authored first:

```sh
dotnet test tests/Orbit.Application.Tests --filter 'FullyQualifiedName~ProactiveWriteReplay_UnsupportedClient|FullyQualifiedName~ProactiveWriteReplay_DeclaresOnlySupportedOfferFields' --logger 'console;verbosity=minimal'
dotnet test tests/Orbit.Infrastructure.Tests --filter 'FullyQualifiedName~TruncatedAuxiliaryResponse_WarnsWithPurpose|FullyQualifiedName~Completions_EnforcePurposeCapForDirectAndBudgetedCalls' --logger 'console;verbosity=minimal'
```

Both commands exited one. The first produced seven failures and one pass, including
six recorded single/bulk chat writes rejected as `offers_not_supported`. The second
produced four failures and thirteen passes: the summary cap was 2,048 rather than 600,
and all three purpose-aware truncation warnings were absent. With the fix, the focused
groups passed: 233 Infrastructure, 131 Application and four Domain cases.

The broader Application suite exposed a requested-field provenance regression in
`R9f_StrictPlaceholdersPreserveValidSuggestionsAfterRequiredRepair`. The unchanged
test failed; the additional direct-preview test below then failed in all three cases:

```sh
dotnet test tests/Orbit.Application.Tests --filter 'FullyQualifiedName~OfferClients_EmptySuggestionPlaceholders' --logger 'console;verbosity=minimal'
```

The fix captures requested-field provenance before create-tool defaults are derived.
This early preparation applies only to new drafts; existing-record ownership denials
retain their original position before preview preparation. The unchanged ownership
regression test exposed the ordering issue, and all 96 focused preview/repair/ownership
cases pass with that restriction.
It preserves empty-placeholder normalization for unsupported clients. The focused
preview/repair group then passed all 74 cases, and the catalog contract group passed
all twelve cases after its expected initial catalog was corrected to omit `ask`.
