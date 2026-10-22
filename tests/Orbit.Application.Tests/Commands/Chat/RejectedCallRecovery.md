# Server-certified rejected-call correction

Tracks thomasluizon/orbit-tickets#418.

## Enforcement owner

`ProcessUserChatCommand.Correction.cs` owns the correction invariant in
`RejectedCallCorrection`: a candidate must have exactly one equivalent prepared
intent among all original occurrences, including successful siblings. That occurrence
must carry the gateway's `RejectedBeforeDispatchResponse`. One correction round can
spend each failed occurrence at most once. Ambiguous, repeated, unrelated or
uncertified candidates cause no dispatch and consume no failure.

The model supplies candidate arguments. The gateway supplies the original prepared
intent and the evidence that its argument check rejected the operation before policy
reservation, preview or execution. A tool name routes preparation; neither a matching
name nor a matching action establishes identity. Model call identifiers and
`retry_of` never establish identity. Unknown arguments fail preparation.

Only eligible failures receive a server-authored `payload.correction_allowed=true`
offer on the existing continuation. The prompt permits a candidate only for those
offers; ordinary failure, clarification and internal-error-copy rules remain intact.
The offer is not a certificate, an executable argument or a reusable retry identity.
The existing completion client carries the boolean through its ordinary tool result
serialization. It is exercised with messages produced by the loaded Responses SDK.

Dispatch takes the original request and the original preparation's execution
arguments. Candidate arguments never become the execution payload. Authentication,
scopes, ownership, preview, approval, revision, idempotency and transaction ownership
continue through the existing operation executor. Successful outcomes come from that
executor; pending approval produces a proposal rather than a saved-action claim.
After an accepted correction, the response ends with these authoritative actions and
approval cards, using their existing localized messages. It requests no additional
model prose, so an invented saved-action sentence cannot replace a held outcome.

## Preparation and rejection contracts

`IPreparedIntentTool` is implemented by the habit creator and all seven exposed
multiplexers. `PreparedToolIntent` contains a concrete command or a typed habit
creation tree. Command record equality includes the concrete command type and every
command value. Immutable value sequences preserve complete collection contents.
Habit preparation reuses the same `HabitCreateParams` builders as execution, including
inline children, associations, inherited cadence and dates. It compares domain
parameters rather than JSON equality or a handler-maintained field allowlist.

The gateway consumes the preparation's validation of the original arguments before
policy for a prepared, unconfirmed chat mutation. The same validation result avoids
repeating an existing command check. Only failure at this specific point issues the rejection subtype.
Ordinary failed tool results, thrown exceptions, cancellation after dispatch, partial
batches, ownership denials and preview failures cannot issue it. Evidence is an
in-process response subtype, excluded from serialization, not a public argument or
provider response field. Approved operations keep their existing payload binding.

After successful preparation and validation, the gateway uses the tool's prepared
arguments for preview or execution. This freezes omitted user-local dates and child
inheritance before another read of "today" could change the requested schedule.

## Useful correction classes

The habit preparer can interpret exact `HH:mm:ss` values whose seconds are zero and
produce the existing `HH:mm` execution representation. For example, `21:00:00` is
rejected by the real field checker, while its prepared intent equals `21:00`.
Nonzero seconds and ambiguous text cannot be normalized without information loss
and do not receive a certificate.

The existing weekly-with-weekdays and daily-with-weekdays representations prepare
to the same actual domain schedule. The historical maintenance payload already
succeeds; it is not described as a current shape rejection. A separate injected
pre-dispatch rejection exercises its one allowed correction while preserving the
successful adaptation sibling.

The seven multiplexers prepare their actual commands, including setter direction,
explicit target IDs, credentials, scopes, checkout interval and collection contents.
They can certify equivalent source representations after a known pre-dispatch
rejection, such as equivalent GUID representations or explicit defaults. A different
valid command is not an equivalent intent. Preparation itself does not dispatch
commands or save entities.

## Deliberate fallback limits

| Owner | Ineligible cases and reason |
| --- | --- |
| `UpdateNotificationsTool` | `mark_all_read` and `test_push` have dynamic target sets. |
| `DeleteNotificationsTool` | `delete_all` has a dynamic target set. An executed explicit-ID batch can partially save and is never certified after execution. |
| `ManageCalendarSyncTool` | `run_sync` selects targets dynamically; an empty calendar selection means all owned calendars. |
| `ManageAccountTool` | `reset_account` has a broad target set. `confirm_deletion` checks security attempts and cannot be treated as preparation without effects. |
| Habit creator | Missing semantic information, invalid or lossy representations, unknown fields and requests needing clarification cannot establish a prepared intent. |
| Other write tools | No preparation owner means no correction certificate. |

These limits do not replace the useful habit correction. They retain the existing
truthful failure when an executed call rejects. Existing validation and approval
owners continue to handle unsupported arguments. Preparation does not turn an
unvalidated held proposal into an executed success.

Correction uses the existing continuation port, tool iteration ceiling, cumulative
token limit, daily-message charge, deadline and cancellation token. It opens no
second loop and changes no model, effort, ceiling or provider client. If there is
no remaining allowance, the original failure is returned. A second rejection ends
the attempt. Text-only recovery cannot claim success; additional tool calls after
the corrected outcome cannot start another write round. Block-answer reservations
and requests carrying approval tokens retain their existing failure path.

## Owning acceptance and fixture provenance

`ProcessUserChatCommandHandlerTests.Correction.cs` drives the real handler, real
preparers and real Infrastructure gateway, with repository and policy ports
substituted. It covers useful rejection and correction into a real preview, two
saved historical habits after an injected no-effect rejection, all seven
multiplexers, all five unsafe-candidate classes, changed targets, duplicate and
ambiguous candidates, successful-sibling non-replay, localized attempt exhaustion,
partial saved deletion, uncertain exceptions, model allowance failure, cumulative
token capacity, tool-round capacity and linked cancellation.

`PreparedToolIntentTests.cs` exercises concrete command and domain preparation
without dispatch, including collections, broad-selector exclusions, security-check
exclusion and inherited child schedule materialization. Discovery requires every
exposed multiplexer to declare its preparation owner.

`AgentToolCorrectionTests.cs` uses the real policy evaluator, pending-operation
store, previewer and revision service with EF's in-memory provider and repository
ports. It proves approval remains required, a token cannot execute twice, a
revision invalidates an earlier approval, ownership denial cannot become a
certificate, MCP cannot mint chat evidence and proof stays off the wire.

The original sixteen `RecoveryBoundary_` cases are unchanged. Their discovery check
requires the exact exposed multiplexer fixture set, so an eighth exposed multiplexer
cannot silently escape coverage. `manage_feature_flags` remains MCP-only.

The two historical payloads are reused verbatim from
`ProductionAdaptationArguments` and `ProductionMaintenanceRejectedArguments` in the
existing handler tests. No live audit database was read. Other payloads are synthetic,
derived from actual repository schemas, command producers and validators. The time
normalization fixture changes only a source-supported time representation; it is
not described as a captured production rejection. No external response field was
introduced or inferred from a test double.

## Observed red and green controls

Before source or test edits, this unchanged command passed nineteen cases:

```sh
dotnet test tests/Orbit.Application.Tests --filter 'FullyQualifiedName~RecoveryBoundary_|FullyQualifiedName~Handle_RejectedCreateHabit_AttemptsMutationOnceAndReturnsLocalizedFailure|FullyQualifiedName~Handle_ProductionTwoHabitFailure_PreservesSuccessfulSiblingAndSanitizesFailedAction' --nologo
```

The prior capability probe was then run against the exact starting runtime:

```sh
dotnet test tests/Orbit.Application.Tests --filter 'FullyQualifiedName~RecoveryCapability_' --nologo
```

It exited one: expected two gateway dispatches, found one. Its source SHA-256 is
`cee5a08bd337d10fe5f83fea9ebc453dcd79c29d707328a4d658afe0c6082236`.
That synthetic probe has been replaced in the owning suite by the real preparation,
rejection, second dispatch and preview acceptance under the same test name.

Two temporary faults in the actual central owner were also observed:

| Fault | Filter | Result |
| --- | --- | --- |
| Make `RejectedCallCorrection.CanRecover` return false | `FullyQualifiedName~RecoveryCapability_RejectedOccurrencePreparesOneCorrectedPreview` | Exit one; expected two dispatches, found one. |
| Remove prepared-intent equivalence from occurrence selection | `FullyQualifiedName~RecoveryCapability_OppositeSetterOrForgedIdentityCannotConsumeFailure` | Exit one; the opposite-setter candidate causes an extra dispatch. The server still uses its original prepared arguments. |

Each used `dotnet test tests/Orbit.Application.Tests --filter '<filter>' --nologo`.
The central source was restored byte for byte after each fault. Its SHA-256 before
and after both controls was
`69780abd663de8c763ee51a9b73e7d90ffdfb730b8830cadb669ab88c5ab6ac3`.
The focused owning suites pass with the guards restored. The removed historical
unsafe implementations remain absent; no historical red result is claimed for them.

The unchanged `ToolFailureSectionTests` also passed before the prompt exception was
added. `ToolCorrectionPromptTests.BuildAllowsOnlyServerOfferedCandidatesAndRetainsOrdinaryFailureRules`
then failed against that unchanged prompt because the server-offered candidate path
was absent. Its command was:

```sh
dotnet test tests/Orbit.Infrastructure.Tests --filter 'FullyQualifiedName~ToolCorrectionPromptTests.BuildAllowsOnlyServerOfferedCandidatesAndRetainsOrdinaryFailureRules' --nologo
```

After the narrowly scoped exception, the ordinary prompt, candidate prompt,
SDK-produced serialization and gateway tests pass together. Completion clients and
their existing transport, budget and token ownership are unchanged.

The unchanged approval owner tests also caught redundant command checks during
implementation: the `name` and nonempty `selection` cases of
`CapabilityParityWrites_ShowPreviewBeforeApprovalAndDispatchOnlyAfterConfirmation`
failed with three checks where one was expected. Reusing the preparation's original
validation result restored the single check. The existing approval test file was
not edited. Both cases pass in the focused approval suite with the correction and
the original reservation, revision and approval owners enabled.

This capability requires an ordinary API deployment. It needs no configuration,
secret, schema migration, backfill or mobile contract change. Local verification
does not claim the production payload-construction cause is fixed. Stryker remains
owned by CI.
