# Selecting an OpenAPI artifact

[artifacts.json](artifacts.json) is the checked publication catalog. Generation,
freshness and compatibility use the same declarations. Each entry contains exactly
`id`, `httpVersion`, `revision`, `documentName`, `path`, `deprecated` and `replacement`.
The first five are strings and form the stable identity. `deprecated` is a boolean;
`replacement` is another identity or null. Paths are repository-relative files.

| Identity | HTTP header selection | Revision | Repository path |
| --- | --- | --- | --- |
| `orbit-http-v1-r1` | omitted or `api-version: 1.0` | deprecated legacy | `src/Orbit.Api/openapi.json` |
| `orbit-http-v2-r1` | `api-version: 2.0` | deprecated legacy | `src/Orbit.Api/openapi_v2.json` |
| `orbit-http-v1-r2` | omitted or `api-version: 1.0` | corrected enums | `src/Orbit.Api/openapi_v1-r2.json` |
| `orbit-http-v2-r2` | `api-version: 2.0` | corrected enums | `src/Orbit.Api/openapi_v2-r2.json` |

The legacy files retain known inaccuracies: FrequencyUnit, DayOfWeek and
InstanceStatus promise integer-only output; ScheduledReminderWhen advertises
snake_case names instead of MVC's canonical response names. Their replacements
are the corresponding r2 entries. Deprecation is catalog metadata so existing
artifact bytes retain their exact meaning. Legacy retirement needs separate
consumer adoption work. Future compatible endpoint additions can still update
all supported streams through ordinary generation and comparison.

Chat clients may send optional `clientContext.supportsMessageAllowance: true` to
receive optional `messageAllowanceConsumed` on each successful chat response,
including the SSE final response. True means the request consumed one message;
false means it consumed none. The value follows the allowance reservation, so
cached replies, block replays, pick-list previews, moderated declines and crisis
replies report false. A fresh choice answer or model turn reports true when its
reservation succeeds. Omission or false for the capability omits the response
field and preserves the typed HTTP 400 moderation refusal (SSE error status 400
on streams). Self-harm flags and phrase-guard crises keep returning support as
successful turns for every client. Deploy the API before the paired consumer in
thomasluizon/orbit-tickets#1708 advertises this capability.

`PUT /api/habits/{id}` accepts optional nullable `tagIds` in both HTTP versions.
Omission or null preserves tags, an empty array clears them, and a supplied array
replaces the assignment together with core fields, checklist and requested goals.
The submitted count includes duplicates for the existing tag limits; resolved
assignments form a set. Every requested tag and goal must resolve for the owner.
Success remains HTTP 204 with no body. Independent tag and template management
keeps its own commit boundary. A transport timeout requires readback or supported
replay to reconcile the outcome. Deploy this producer before the paired consumer
sends the field. Tracks thomasluizon/orbit-tickets#1605.

`PUT /api/habits/{id}` also accepts optional nullable `parentId` (UUID) and
`clearParent` (boolean). Omitted or null `parentId` preserves the existing parent
unless `clearParent` is true. A non-null `parentId` sets the owned target; false
or null `clearParent` does not suppress that set. True `clearParent` with a null
or omitted `parentId` makes the habit top level. Supplying both true `clearParent`
and a non-null `parentId` is rejected. Ownership, cycles, subtree depth, final
General compatibility and live-root capacity are validated under the same user
lock and transaction as the complete edit. The standalone parent endpoint retains
its null-means-root contract and operation schemas.

Ordinary habit detail and full detail append nullable UUID `parentId`. Both reads
emit explicit null for an actual root and the authoritative parent UUID for a
child. Absence from an older producer means unknown. Success for the combined
update remains HTTP 204 with no body. Deploy this producer before publishing the
paired parent-selector consumer. Tracks thomasluizon/orbit-tickets#1608.

An immutable consumer selection is a full 40-character producer commit plus an
exact catalog identity. Read the catalog at that commit, verify the identity,
HTTP version and path, then read the artifact at the same commit. Reject unknown
identities, mismatched versions, missing files and malformed commits. There is no
arbitrary URL selector or fallback to another artifact. Existing SHA-only pins
continue selecting `openapi.json`.

These are documentation revisions for the unchanged HTTP APIs. Revision selection
does not change request headers, routes, defaults or runtime DTOs. The new schemas
publish canonical output names plus undefined Int32 numeric fallback, and separate
request components for case-insensitive names, Unicode surrounding whitespace,
comma-separated names and bounded signed/zero-padded numeric strings. Only reminder
inputs additionally accept the exact `same_day` and `day_before` aliases. Binding
success still does not imply validator or domain acceptance.

JSON Schema integer describes a numeric value. It cannot distinguish JSON tokens
`1`, `1.0` and `1e0`; MVC's enum converter rejects the latter two spellings. Tests
record this lexical limitation separately. Numeric branches explicitly bound values
to Int32; numeric-string patterns also reject overflow. Runtime acceptance is unchanged.

Run from the repository root:

```sh
dotnet build Orbit.slnx
python3 .github/scripts/openapi_artifacts.py freshness
python3 .github/scripts/openapi_artifacts.py plan --base origin/main
python3 .github/scripts/openapi_artifacts.py evidence
dotnet test tests/Orbit.Infrastructure.Tests --filter 'FullyQualifiedName~HabitEnumContractTests|FullyQualifiedName~ReminderInputContractTests|FullyQualifiedName~HabitScheduleContractTests|FullyQualifiedName~ApiVersioningTests'
python3 -m unittest discover -s .github/scripts -p 'test_openapi_artifacts.py'
```

Freshness requires every catalog artifact to be committed, present and identical to
normal generation, including the generator's complete file list. Run generation
again and require no artifact diff. The compatibility planner reads the actual target
commit and its catalog, with the two historical legacy paths declared explicitly
when that catalog predates publication. Published identities and paths cannot be
removed, renamed or substituted. Only an artifact genuinely absent from that target
is a first publication with no prior same-stream comparison.

The required `OpenAPI Breaking-Change Gate` retains the pinned v1 action and ERR
failure threshold. It also requires the inventory and every additional stream's
ordinary comparison to succeed. Every later r2 change compares against its own
target-branch file. The existing severity configuration is unchanged. Failure,
cancellation or skipping of a dependent comparison cannot satisfy the required gate.

Publication tooling reconstructs canonical paths from validated ASCII catalog
identities and rejects files whose symlinks escape the repository. Generator-cache
entries must equal the complete canonical absolute output paths; tooling never
resolves paths supplied by cache contents. Target refs use ASCII letters, digits,
slashes, dots, underscores and hyphens, starting with a letter or digit. Git calls
use fixed subcommands and an end-of-options boundary. Resolved commits must be full
40-character hexadecimal values before any target inventory or artifact is read.

Tracks thomasluizon/orbit-tickets#1591. The blocked UI adoption ticket
thomasluizon/orbit-tickets#1601 owns selector, pin, snapshot, Orval, Contract Drift,
rebaseline workflows and consumer instruction changes after API acceptance and
deployment. The producer commit and this catalog/evidence are its handoff inputs.
UI PR #1913 retains its routine generated-pair scope. The model work in #227 is
separate; #1558 remains closed and #1513's completed consumers remain intact.

The append-only habit ranking query, paging, refresh and `habitRanking` block
contract for thomasluizon/orbit-tickets#1618 is documented in
[HabitRanking.md](HabitRanking.md). It preserves retrospective semantics and
requires API deployment before client capability advertisement.

The append-only negotiated `gamification` read block for profile-only,
achievement-only and other partial gamification selections is documented in
[GamificationReadBlocks.md](GamificationReadBlocks.md). Combined reads preserve
the existing `streak` contract. Deploy this producer before the carrying consumer
advertises the new kind. Tracks thomasluizon/orbit-tickets#1631.

The append-only proactive `parts` field and capability negotiation for
thomasluizon/orbit-tickets#1662 are documented in
[ProactiveAnswerParts.md](ProactiveAnswerParts.md). The existing chat read blocks
cover every proactive trigger. Publish the producer before updating the paired
consumer's shared snapshot and enabling proactive block rendering.
