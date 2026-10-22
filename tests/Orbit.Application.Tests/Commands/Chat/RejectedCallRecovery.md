# Rejected-call recovery: proof boundary

Tracks thomasluizon/orbit-tickets#418.

This is the bounded design/reproduction stage. The dispatcher retains its existing
failure stop. These tests protect that boundary; they do not implement recovery or
establish that the ticket's acceptance criteria are complete.

## Required invariant

An accepted correction must belong to a server-owned failed occurrence that is
known to have made no changes, and must preserve that occurrence's prepared intent.
An already successful mutation cannot execute again. An unbound candidate cannot
consume a failure, create a proposal, or contribute a success claim. A corrected
call must use the existing operation executor and approval path, at most once.
An uncertain execution outcome ends recovery.

## Why the current contracts cannot prove it

`src/Orbit.Application/Chat/Tools/IAiTool.cs` supplies parameter schemas and optional
`IArgumentCheckTool` validation. Neither produces a canonical prepared intent, a
certified correction, or evidence that two valid argument sets mean the same thing.
`AiToolRegistry` stores tools and controls chat visibility; it declares no semantic
equivalence contract. A schema's type, enum, nullability and required fields constrain
validity, not unchanged intent.

`RecoveryBoundary_ValidationAcceptsBothOppositeSetterIntentsWithoutWriting` uses the
real argument checker, command preparation and validators. Both
`{"action":"set_auto_sync","enabled":true}` and its `false` counterpart validate
without writing. Validation cannot distinguish a corrected representation from an
opposite instruction. Restricting a patch to a rejected occurrence establishes its
parentage, but still permits that change. An echoed `retry_of` adds no independent
evidence and is not a registered tool argument.

`src/Orbit.Infrastructure/Services/AgentOperationExecutor.cs` maps ordinary tool
rejections and caught execution exceptions to `AgentOperationStatus.Failed`.
`AgentOperationResult` has no rejection phase or guarantee that no effect occurred.
For example, `DeleteNotificationsTool` can issue several deletion commands and then
return failure. `ITransactionalAiTool` is an explicit opt-in, and tools can also
have external effects. Replaying an arbitrary failed operation can therefore repeat
a committed effect even if the replacement JSON is identical.

`RecoveryBoundary_FailedBatchCanContainAnAlreadySavedMutation` drives the real
notification tool and first deletion command handler, with repository ports
substituted. A rejection injected at the second command leaves the first entity
deleted and its save already called. This proves the orchestration can return a
failure after a mutation; it does not claim a live database reproduction.

The gateway's `PreviewOnly` mode prevents execution when policy allows a call, but
is not a universal semantic validator or correction certificate. The argument check
before policy is selective, and a preview of a changed value only describes that
new proposal. A preview fingerprint binds approval to a proposal; it does not prove
that a replacement preserves the original request.

## Approaches assessed

| Approach | What it proves | Missing proof |
| --- | --- | --- |
| Handler applies a model patch to the original JSON | Exact parent occurrence | A patch can reverse `enabled`, change an action or replace a target. |
| Preserve every original argument value | No value changes | Rejects legitimate semantic normalization and cannot recover an invalid or missing value. |
| Match names, actions, overlapping fields or a model retry identifier | Model-selected association | Neither unchanged intent nor known absence of effects. |
| Validate or preview the candidate before dispatch | Candidate validity or a held proposal | Both opposite setters can be valid; unknown outcomes remain unsafe to repeat. |
| Tool-owned preparation plus typed no-effect rejection | A place for server-certified equivalence and execution evidence | These contracts do not yet exist and require a separately agreed implementation scope. |

The recommended next scope is tool-owned semantic preparation and a typed rejection
that guarantees no write occurred, carried through the operation gateway. Recovery
would be unavailable by default. A tool would have to certify that its correction
preserves its prepared intent, rather than supply a list of fields for a handler
matcher. The handler would own the occurrence, attempt count and accepted outcomes;
the model could propose a candidate but could not certify it. Tools with no proof
would retain the localized failure. This scope needs agreement before changing
dispatch; it is not implemented by this test-only stage.

## Fixture provenance and current behavior

The two historical JSON payloads are reused verbatim from
`ProcessUserChatCommandHandlerTests.cs`: `ProductionAdaptationArguments` and
`ProductionMaintenanceRejectedArguments`. No audit database was queried.

The real `CreateHabitTool` already accepts both. Its `NormalizeWeekdayUnit` converts
`Week`, quantity one and explicit weekdays to the existing daily-with-weekdays
representation. `RecoveryBoundary_HistoricalPayloadsAlreadyCreateTwoHabitsWithTheirOriginalSchedules`
executes the real tool and domain factories and checks both saved schedules. Calling
that payload a present-day production rejection would be incorrect. Existing failure
tests and the new boundary tests deliberately simulate gateway rejection.

The seven multiplexer pairs are synthetic adversarial examples derived from the
actual tool implementations and their parameter schemas, not captured production
arguments. Their names and values are checked against those schemas. The tests
substitute the operation executor port, make the first dispatch fail, and queue a
replacement followed by a false success sentence. They assert actual dispatch count,
preserved failed operations, original successful siblings and truthful final copy.
These are failure-boundary guards, not end-to-end approval or persistence evidence.

Discovery scans every concrete Application write tool's actual action enum. The
exposed set must equal the fixture set, so a new exposed multiplexer fails coverage
until it has a case. The seven ticket-named multiplexers remain exposed.
`manage_feature_flags` is an additional MCP-only multiplexer: direct lookup remains
available, while `GetAll()` excludes it. It is not exposed to chat for a recovery test.

## Reproduction evidence

Before adding tests or changing source, the unchanged owning tests passed:

```sh
dotnet test tests/Orbit.Application.Tests --filter 'FullyQualifiedName~Handle_RejectedCreateHabit_AttemptsMutationOnceAndReturnsLocalizedFailure|FullyQualifiedName~Handle_ProductionTwoHabitFailure_PreservesSuccessfulSiblingAndSanitizesFailedAction' --nologo
```

Exit zero, three cases passed. They deliberately specify one dispatch and localized
failure, so they cannot prove the missing correction capability.

A temporary capability probe, `RecoveryCapability_RejectedOccurrencePreparesOneCorrectedPreview`,
then queued a source-derived weekday correction after a synthetic gateway rejection.
It failed on unmodified production source: expected two gateway dispatches, found
one. Command:

```sh
dotnet test tests/Orbit.Application.Tests --filter 'FullyQualifiedName~RecoveryCapability_' --nologo
```

Exit one, one assertion failure. The probe source and log are retained in the local
verification artifacts, outside the committed test suite, because the capability
is deliberately not implemented at this stage. Its source SHA-256 is
`cee5a08bd337d10fe5f83fea9ebc453dcd79c29d707328a4d658afe0c6082236`.
The five removed unsafe implementations cannot be replayed from this base without
reintroducing an unsafe dispatcher; those five historical reds remain unobserved.

Two temporary faults in real owners established that the new guards are causal:

| Fault | Focused filter | Observed result |
| --- | --- | --- |
| Make `CreateHabitTool.NormalizeWeekdayUnit` return its input unchanged | `FullyQualifiedName~RecoveryBoundary_HistoricalPayloadsAlreadyCreateTwoHabitsWithTheirOriginalSchedules` | Exit one: the historical maintenance arguments fail real validation. |
| Make `AiToolRegistry.GetAll` include MCP-only tools | `FullyQualifiedName~RecoveryBoundary_FixturesCoverEveryExposedMultiplexerAndKeepMcpOnlyToolsHidden` | Exit one: `manage_feature_flags` appears in chat declarations. |

Each filter used `dotnet test tests/Orbit.Application.Tests --filter '<filter>' --nologo`.
Both faults were restored before the retained suite and build. The dispatcher's
source was never edited. The restored SHA-256 values are:

- `CreateHabitTool.cs`: `17b5b22d9b591f2fb020f8377aad7db10ce7f1a96ee31a352f23b826335320f8`.
- `AiToolRegistry.cs`: `bf0bbccb4e7329a7acfee4ee2d6782e9c12b1af9dae953b0d08db2cb3f2ef4bf`.
- `ProcessUserChatCommand.Tools.cs`: `38a3f9a656ce561c9ff499b2517bf7aa123dc6ae9c4552101a55b668240fe5b2`.

The retained suite filter is `FullyQualifiedName~RecoveryBoundary_`. All outcome
records and argument fields read by these tests are repository-owned contracts.
No new external API field or provider response shape is assumed.

## Remaining capability acceptance

The five unsafe candidates have current failure-boundary guards. Their historical
unsafe implementations are absent from this base; a passing guard is not evidence
that a corrected recovery implementation has passed the required red/green cycle.
Useful recovery, two-habit recovery after an injected rejection, one-correction
exhaustion, central runtime enforcement and safe replay after rejection remain open.
The existing budgeted intent route, model, ceiling, token and tool-round limits,
held-write approval/revision, ownership, idempotency and uncertainty behavior must
remain intact when those missing contracts are implemented. No deployment, setting,
secret or migration is needed for this test-only stage.
