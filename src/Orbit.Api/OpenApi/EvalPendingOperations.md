# Staging eval pending-operation cleanup

Tracks thomasluizon/orbit-tickets#1845. The paired harness work belongs to
thomasluizon/orbit-tickets#1822; this producer change does not update that repository.

The owner opts disposable accounts into this fixture by setting
`StagingEval__AccountIds` to comma-separated account UUIDs in the staging API
service's Render Environment screen. The service is the target selected by
`RENDER_STAGING_SERVICE_ID` in the GitHub `staging` environment. Deploy the API
through the repository's release workflow with `environment=staging`, and apply
the environment setting before the harness uses the routes. An absent or empty
allowlist disables access. Remove an account from the setting and apply the
environment change to revoke access.

Both routes require an authenticated first-party JWT session for an explicitly
allowed account and `ASPNETCORE_ENVIRONMENT=Staging`. API keys and OAuth credentials
cannot use them. Production and Development reject them even with an allowlist.
The account always comes from the session, with no request account selector.
Unauthenticated requests receive 401; callers outside the fixture boundary receive
403. Responses cannot be cached. The fixture has REST cross-references in the
pending-operation capability catalog but is not exposed as a chat or MCP tool.

## Contract

Both HTTP versions support `/api/eval/pending-operations`. The request has no body.
Select either version with the existing `api-version` header; omission selects v1.

`GET` returns HTTP 200 and an authoritative, uncached database count:

```json
{"pendingCount":0}
```

`DELETE` cancels every unconsumed stored operation owned by that session account,
then returns HTTP 200 with the number cancelled by that request:

```json
{"cancelledCount":3}
```

The count includes expired operations, already-confirmed operations, old argument
schemas, every operation kind, and change-set owners and their member rows.
Consumed rows are retained as history. The fixture does not depend on conversation
recording, response exports, known preview IDs, tool argument parsing or the
awaiting-preview enumeration, which suppresses members and confirmed operations.
Cancellation clears confirmation token hashes and marks the rows consumed. It
does not confirm or execute a write. Repeating `DELETE` on an empty account returns
`{"cancelledCount":0}`. Other accounts are unaffected.

Concurrent revisions or consumption can cause HTTP 409 after the bounded
concurrency retry. Failed requests do not establish isolation. Cleanup also does
not undo writes already executed before it started.

## Harness handoff

Deploy the producer and obtain owner approval for the exact disposable account
before enabling its allowlist entry. Verify access using that account's session
against `https://api-staging.useorbit.org`. Confirm an unlisted account and an
API key cannot use either route. No database migration is required.

Give each running eval exclusive use of its account. Stop concurrent chats and
pending-operation execution before cleanup; a new proposal after readback can
invalidate isolation. Before every attempt, including retries and process restarts:

1. Call `DELETE` and require HTTP 200 with an integer `cancelledCount >= 0`.
2. Independently call `GET` and require HTTP 200 with integer `pendingCount === 0`.
3. Abort the attempt on any denied, failed, malformed or nonzero readback. Never
   infer isolation from a missing field, an empty export or a cancellation count.
4. Create proposals during the attempt, then repeat both calls before the next
   attempt. Check that earlier previews and confirmation tokens cannot be used.

The published OpenAPI artifacts contain the response schemas.
`StagingEvalPendingOperationsTests` serializes the actual controller responses with
MVC JSON options and repeats this sequence across fresh database scopes using the
real handlers and repository. It covers all catalog operation names, old and expired
previews, confirmation-token invalidation, no conversation history and account
isolation. These are producer unit observations, not a live staging response capture.
The UI worker must pin the deployed producer contract and capture real responses
before adding its harness fixtures. API deployment precedes that consumer change.
