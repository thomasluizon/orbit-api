# Server-certified rejected-call correction

Tracks thomasluizon/orbit-tickets#418.

`RejectedCallCorrection` in `ProcessUserChatCommand.Correction.cs` owns the invariant:
only one uniquely bound, server-prepared occurrence rejected before dispatch may be
corrected once. Complete intent equality includes successful siblings. Dispatch uses
the original preparation and request, and only gateway outcomes establish success.
Unrelated, ambiguous, repeated or uncertified candidates consume no failure.

| Reviewed guard family | Required behavior |
| --- | --- |
| Rejection offers | Only a failed `RejectedBeforeDispatchResponse` receives `correction_allowed`; successful siblings keep their actual payload. |
| Mixed failures | An uncertain or executed failure anywhere in the original write set closes recovery, even beside a certified rejection. |
| Intent binding | Real command and domain preparation must preserve the complete intent, including action, targets, collections and setter direction; model identifiers prove nothing. |
| Candidate validation | A prepared candidate with failed argument validation cannot reach dispatch. |
| List preservation | Empty list entries cannot disappear during preparation and make a lossy candidate appear equivalent. |
| Cancellation | Cancellation before continuation or during candidate preparation prevents the next external call or dispatch. |
| Successful corrections | Every uniquely rejected sibling can recover once; successful originals cannot replay. |
| Second rejection | A failed corrected dispatch stops remaining candidates and leaves every unresolved outcome visible. |
| Failure reporting | Text-only, rejected-candidate and provider-failure paths retain the plain localized message and sanitized failed action. |
| Streaming | Correction requests suppress model deltas so invented success cannot reach the user before an authoritative outcome. |
| Policy outcomes | A corrected operation denied by policy retains both its failed action and policy denial; failed payloads cannot advertise successful related surfaces. |
| Authority | Correction preserves the original request, step-up requirement and JWT scope semantics. |
| Approval persistence | A corrected proposal requires a nonempty real preview and a persisted binding; a failed binding produces failure without an approval card. |
| Shared allowance | Correction uses the existing iteration and cumulative-token ceilings; an exactly full cumulative token allowance remains usable. |
| Equivalent defenses | Terminal-only replay bookkeeping, repeated cancellation checks, caught missing preparations and value-hash collisions cannot authorize another correction or alter its user-visible outcome. |
| Other paths | Read-card selection, ordinary single-to-bulk proposal supersession, block-answer checkpoints, approval-token revalidation, clarification, audit logging and concurrency retries of unprepared tools have separate owners. |

`ProcessUserChatCommandHandlerTests.Correction.cs` and
`ProcessUserChatCommandHandlerTests.RecoverySurvivors.cs` drive the real handler,
preparers, binder and gateway with substituted repository and policy ports.
`PreparedToolIntentTests` and `RecoveryBoundary_` retain discovery coverage of all
seven exposed multiplexers. `AgentToolCorrectionTests` exercises the real policy,
approval store, previewer and revision service.

The two historical habit payloads reuse `ProductionAdaptationArguments` and
`ProductionMaintenanceRejectedArguments` verbatim. Other inputs are explicit
source-derived variations, including zero-second time formatting and blank tag
entries. Injected pre-dispatch and post-dispatch failures are named as synthetic.
No new external response field or production audit fixture is inferred.

Each guard-gap replacement is applied separately, observed red, restored byte for
byte, and observed green. The complete per-mutant classification and execution
receipts belong in the review report. Stryker thresholds and mutation scope remain
unchanged. The Infrastructure campaign also selects the Application test project
so gateway mutants run the real handler and binder guards. The complete mutation
campaign belongs to the root runner or CI.
