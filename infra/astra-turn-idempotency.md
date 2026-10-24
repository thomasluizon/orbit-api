# Astra turn identity and replay

Tracks thomasluizon/orbit-tickets#1675 and enables the separately released consumer in
thomasluizon/orbit-tickets#1672. Release this API and its additive migration before
enabling consumer retries. Older producers emit `started` without a turn identity
and cannot guarantee safe automatic retry. The JSON chat endpoint retains its existing
behavior; this contract applies to `POST /api/chat/stream` in both HTTP versions.

## Request identity

Send an opaque `Idempotency-Key` header containing 1 to 200 characters, with at least
one non-whitespace character. Generate it once per submitted turn. A single automatic
resend must reuse that exact key and the same message, image bytes and MIME type,
history, client context, confirmation token, block answer and eval-trace choice.
Multipart boundaries and image filenames do not form part of identity. Keys are
scoped to the authenticated user; the fingerprint also binds authorization method,
granted scopes and read-only status. JSON object property order is canonicalized.
Correlation identifiers and stream callbacks do not form part of identity.

The database uniquely reserves `(UserId, KeyHash)` before sending an acknowledgement
or dispatching the command. The reservation never expires for reuse. Its expiry
instant limits waiting for execution, rather than permitting another execution.
Payload conflicts return HTTP 409 JSON before SSE starts:

```json
{"code":"CHAT_TURN_KEY_CONFLICT","error":"The turn key belongs to another payload."}
```

Input validation happens before reservation. Requests without the header keep their
existing stream behavior. First executions retain the per-user distributed concurrency
lease. Duplicate requests join or replay without acquiring another execution lease.
Other HTTP rate limits still apply to each incoming request.

## Acknowledgement and replay

The response is HTTP 200, `Content-Type: text/event-stream`, with
`Cache-Control: no-cache, no-store` and `X-Accel-Buffering: no`. Every JSON event below
is framed as `data: <json>` followed by two newline characters.

The first accepted request emits `started` with `turnId`, `state: "running"` and
`replayed: false`, after the durable reservation. It can emit the existing intermediate
events. The terminal `final` or `error` carries the same `turnId` and a state. The
entire terminal JSON is stored before attempting its network write.

A matching request arriving during execution emits `started` with `replayed: true`
and `state: "running"`. It waits for the durable terminal result, polling the shared
database every 250 milliseconds, then emits that stored event. It does not repeat
earlier intermediate events or execute the command. This also works across instances.
A matching completed request emits `started` with `state: "completed"` and
`replayed: true`, immediately followed by the exact stored terminal event. The terminal
event is unchanged between execution and replay, including correlation IDs, response
parts, block tokens and eval traces. An `error` is terminal and is also replayed.

These complete events were observed from the real controller, stream producer and
SQLite-backed store in `LostAcknowledgement_ConcurrentRetryAndReplay_PersistAndChargeOnce`:

```json
{"type":"started","turnId":"fe4a8c19-9c97-481a-882d-ed7dea936ec0","state":"running","replayed":true}
{"type":"started","turnId":"fe4a8c19-9c97-481a-882d-ed7dea936ec0","state":"completed","replayed":true}
{"type":"final","response":{"aiMessage":"Reply","actions":[],"operations":null,"pendingOperations":null,"policyDenials":null,"correlationId":null,"relatedSurfaces":null,"habitList":null,"goalList":null,"metricsCard":null,"periodInsight":null,"daySummary":null,"streak":null,"calendar":null,"recordLists":null,"accountRows":null,"followUps":null},"turnId":"fe4a8c19-9c97-481a-882d-ed7dea936ec0","state":"completed"}
```

Reproduce the observations without starting a server:

```sh
dotnet test tests/Orbit.Infrastructure.Tests --filter FullyQualifiedName~LostAcknowledgement_ConcurrentRetryAndReplay_PersistAndChargeOnce --logger 'console;verbosity=detailed'
```

The producers are `src/Orbit.Api/Idempotency/ChatTurnStream.cs` and
`src/Orbit.Application/Chat/Models/ChatStreamEvent.cs`. The full response producer is
`ChatResponse` in `src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.cs`.
Optional event fields are omitted; response nullability follows that producer.
These observations exercise in-memory HTTP contexts and real SQLite persistence,
with mocked chat dispatch. They do not claim deployed networking evidence.

## Disconnects and shutdown

After reservation, execution uses the drain deadline and a 240-second turn budget,
independently of the client disconnect token. A failed acknowledgement, delta or final
write does not cancel execution. A disconnected original HTTP pipeline remains active
until execution and terminal persistence finish, keeping its request scope and drain
lease alive. Only the reservation owner dispatches the command, charges usage and
records conversation history. Conversation captures reuse the accepted `turnId`.

The API drain is 240 seconds, the host window is 270 seconds and the production Render
configuration sets `max_shutdown_delay_seconds = 300` in `infra/production.tf`.
The real generic-host SIGTERM test covers both legacy and keyed streams and holds the
reply beyond the default 30-second host window. Deadline tests invoke the production
drain middleware and verify cancellation, terminal persistence and replay without a
second dispatch. Free staging services lack the production shutdown allowance.
See [the shutdown runbook](astra-stream-drain.md) for infrastructure readback.

If execution is cancelled at the supported deadline, the stored error has HTTP status
503 inside the SSE event, code `CHAT_TURN_INTERRUPTED` and state `interrupted`.
If a process is killed before it can store a terminal event, a retry waits only until
the reservation's expiry and receives the same interruption shape. The reservation
is retained, so neither case permits another execution. Effects may be partial;
clients must reconcile them and must not automatically generate a new key. A process
crash cannot atomically commit an external tool's effects and the final stream result.

Final payloads use the existing field encryption converter when configured. The ledger
stores hashes rather than raw keys or request/image content. User deletion cascades
to the ledger. Reservations are retained to prevent delayed re-execution.

## Release sequencing

After merge, run the repository's `Release API` workflow from `main`, first with
`environment=staging`, `branch=main` and the merged full `sha`, then release the same
commit with `environment=production`. Its startup migration creates `ChatTurns` and
its unique index. Verify that the deployed stream emits `turnId` and that a same-key
replay returns the same identity and terminal JSON before enabling #1672 retries.
Read back the production Render shutdown allowance using the shutdown runbook;
repository Terraform configuration alone does not prove it has been applied.
