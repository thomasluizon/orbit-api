# AI turn identity, replay and shutdown

This contract applies to `POST /api/chat/stream` in both HTTP versions. The JSON chat endpoint keeps its existing behavior.

## Request identity

Send an opaque `Idempotency-Key` header of 1 to 200 characters. The key must have at least one non-whitespace character. Generate the key once for each submitted turn. A single automatic resend must reuse the same key. It must also reuse the same message, image bytes and MIME type, history, client context, confirmation token, block answer and eval-trace choice.

Multipart boundaries, image filenames, correlation identifiers and stream callbacks are not part of the identity. The scope of a key is the authenticated user. The fingerprint also binds the authorization method, the granted scopes and the read-only status. The order of JSON object properties is canonicalized.

The database reserves `(UserId, KeyHash)` before it sends an acknowledgement or dispatches the command. The reservation never expires for reuse. Its expiry instant limits the wait for execution. It does not permit a second execution. A payload conflict returns HTTP 409 JSON before the SSE stream starts:

```json
{"code":"CHAT_TURN_KEY_CONFLICT","error":"The turn key belongs to another payload."}
```

Input validation runs before the reservation. A request without the header keeps the existing stream behavior. A first execution keeps the per-user distributed concurrency lease. A duplicate request joins or replays without a second execution lease.

## Acknowledgement and replay

The response is HTTP 200 with `Content-Type: text/event-stream`, `Cache-Control: no-cache, no-store` and `X-Accel-Buffering: no`. Each JSON event is framed as `data: <json>` and two newline characters.

The first accepted request emits `started` with `turnId`, `state: "running"` and `replayed: false` after the durable reservation. The terminal `final` or `error` event carries the same `turnId` and a state. The server stores the whole terminal JSON before it writes the event to the network.

A matching request that arrives during execution emits `started` with `replayed: true` and `state: "running"`:

```json
{"type":"started","turnId":"fe4a8c19-9c97-481a-882d-ed7dea936ec0","state":"running","replayed":true}
```

The request then polls the shared database every 250 milliseconds for the terminal result. It emits the stored event. It does not repeat the intermediate events and does not execute the command.

A matching request for a completed turn emits `started` with `state: "completed"` and `replayed: true`. The exact stored terminal event follows at once:

```json
{"type":"started","turnId":"fe4a8c19-9c97-481a-882d-ed7dea936ec0","state":"completed","replayed":true}
```

The terminal event is the same in execution and in replay. The server also replays an `error` event. The producers are `ChatTurnStream.cs` and `ChatStreamEvent.cs`.

## Disconnects and shutdown

After the reservation, execution uses the drain deadline and a 240-second turn budget. It does not use the client disconnect token. A failed acknowledgement, delta or final write does not cancel execution. A disconnected original HTTP pipeline stays active until execution and terminal persistence finish. Only the reservation owner dispatches the command, charges usage and records the conversation history.

When shutdown starts, the API stops admitting HTTP requests and returns HTTP 503 before the controller acknowledges or dispatches new chat work. An admitted request has up to 240 seconds to finish. A legacy stream keeps its client disconnect token. A keyed stream runs independently of the transport token. The drain runs in the host `StoppingAsync` phase, before Kestrel and the other hosted services stop.

The drain timeout is 240 seconds in `src/Orbit.Api/Hosting/ShutdownDrain.cs`. The host shutdown window is 270 seconds in `src/Orbit.Api/Extensions/ShutdownDrainExtensions.cs`. The production API and web services set `max_shutdown_delay_seconds = 300` in `infra/production.tf`. Terraform owns both settings. The free staging services use the default Render shutdown delay. Render documents the [300 second maximum](https://render.com/docs/deploys#setting-a-shutdown-delay).

A successful drain keeps `started`, the intermediate events and the terminal `final` or `error` event. A client disconnect cancels legacy work at once. A keyed stream continues and keeps the terminal event for replay. When the drain deadline or an earlier host stop deadline passes, the server cancels the remaining request tokens. The cancellation is cooperative, so an unresponsive dependency or a process kill can truncate a reply.

If the supported deadline cancels execution, the stored error has HTTP status 503 inside the SSE event, code `CHAT_TURN_INTERRUPTED` and state `interrupted`. If a process dies before it stores a terminal event, a retry waits until the reservation expiry. It then receives the same interruption shape. The server keeps the reservation, so neither case permits a second execution. The effects can be partial. A client must reconcile them and must not generate a new key automatically. A process crash cannot atomically commit the effects of an external tool and the final stream result.

The unit suite sends a real SIGTERM to a generic host and holds the reply for 31 seconds. It verifies that the host waits for `delta` and `final` in legacy and keyed streams. It uses in-memory HTTP contexts and mocked command dispatch. It does not prove Render networking.

## Storage

The server encrypts final payloads with the existing field encryption converter when it is configured. The ledger stores hashes. It stores no raw key, request content or image content. A user deletion cascades to the ledger.
