# Orbit.Api — HTTP boundary

Controllers, middleware, OpenAPI/Scalar, DI config, `Program.cs`. This project should be thin: it deserializes requests, dispatches to MediatR, and serializes the result.

## Controllers

- One controller per resource: `HabitsController`, `NotificationController`, `AuthController`, `ProfileController`, `TagsController`, `ChatController`, etc. (already exist — match their shape.)
- Inject `ISender` from MediatR. Don't inject repositories or services directly.
- Inject `ILogger<TController>` and log business events at info level.
- Action method = thin pass-through to `_sender.Send(command/query)`. No business logic.
- Use `result.ToPayGateAwareResult(v => Ok(v))` from `Extensions/ResultActionResultExtensions.cs` — never hand-write the 403/PAY_GATE response block.

## Authorization

`[Authorize]` at the controller class level (Roslyn `ORBIT0003` fails the build on a Controller with neither `[Authorize]` nor `[AllowAnonymous]`, class-level or on every action); the pipeline 401s unauthenticated `[Authorize]` requests. Override with `[AllowAnonymous]` per-action only when truly public (e.g., `POST /api/auth/send-code`).

Exempt by construction:
- `GET /health` (mapped via `MapHealthChecks` in `Extensions/WebApplicationExtensions.cs` — no controller)
- `POST /api/auth/send-code`, `verify-code`, `google/code` (AuthController)
- Stripe webhook (verified by signature, not JWT)

## Request validation

FluentValidation runs via the MediatR validation pipeline. Controllers do NOT call validators directly. If validation fails, `ValidationExceptionHandler` middleware converts it to a 400 with field errors.

## OpenAPI / Scalar

Dev-only. `BearerSecuritySchemeTransformer` adds the JWT bearer scheme. New endpoints show up automatically — annotate with `[ProducesResponseType(StatusCodes.Status200OK)]` etc. to document response shapes.

## API versioning

Controllers support v1 and v2 through the `api-version` request header. An omitted header selects v1. Keep the existing routes and declare supported versions on each controller. OpenAPI publishes each supported documentation revision from [`OpenApi/artifacts.json`](OpenApi/artifacts.json). The legacy `openapi.json` and `openapi_v2.json` remain supported, deprecated revisions with known enum inaccuracies. Their corrected replacements are `openapi_v1-r2.json` and `openapi_v2-r2.json`. These revisions describe the same HTTP versions and do not change header selection. Follow [`OpenApi/README.md`](OpenApi/README.md) for immutable consumer selection, generation and publication. Freshness and ordinary compatibility protect every supported stream; the required gate retains the pinned v1 comparison and ERR threshold.

When a change would break an old client, add the new shape under v2 with separate DTOs and `[MapToApiVersion]` actions, and preserve v1. Shared handlers and DTOs must remain compatible with both versions. Retire v1 only after every live client uses v2, enforcing the carrying client build through the `MinSupportedVersion` gate and its release preconditions in `AGENTS.md`.

## Middleware (already wired in `Extensions/WebApplicationExtensions.cs`)

- `SecurityHeadersMiddleware` — nosniff, DENY, referrer-policy, XSS headers on every response.
- `RequestCorrelationMiddleware` — injects correlation ID for log tracing.
- `UnhandledExceptionHandler` — 500 + structured log on uncaught throws.
- `ValidationExceptionHandler` — 400 on FluentValidation failures.

## Security boundaries (Api-level)

- **CORS:** restricted to explicit methods (GET/POST/PUT/DELETE/PATCH) and headers (Authorization, Content-Type). NEVER `AllowAnyHeader()` or `AllowAnyMethod()`.
- **Request size:** 10MB global Kestrel limit. Chat endpoint has its own 20MB multipart limit.
- **Stripe webhook:** MUST verify signatures. Reject if `WebhookSecret` is not configured.
- **Rate limiting:** apply `[DistributedRateLimit]` to abuse-prone endpoints (auth, chat).

## MCP server (built into the API)

`Orbit.Api/Mcp/Tools/*Tools.cs` exposes MediatR commands/queries as MCP tools for the agent surface. When you add a new command/query, decide whether to expose it as an MCP tool — most write operations should be exposed so Astra can perform them.

**MCP tools route MUTATIONS through `McpExecutorBridge` → `IAgentOperationExecutor`** so every mutation gets uniform policy evaluation (read-only-credential denial, scope/plan/feature-flag gates, confirmation + step-up gating) and an `AgentAuditLogs` row. `mediator.Send` inside an MCP tool is permitted ONLY for read queries (`*Query`). Dispatching a `*Command` (mutation) from an MCP tool bypasses that layer and fails the `McpToolsDoNotDispatchCommands` architecture guard (`tests/Orbit.Infrastructure.Tests/Mcp`). Each mutation wrapper forwards the backing chat tool's Name as the `operationId` plus a snake_case argument object matching that tool's schema; destructive/high-risk wrappers forward the caller's `confirmationToken`.

## Patterns to mirror

| Want to add… | Look at… |
|---|---|
| New controller | `HabitsController.cs` (paginated + sub-resources) |
| New endpoint with PayGate | `ApiKeysController.cs` (calls `ToPayGateAwareResult`) |
| New MCP tool | `Mcp/Tools/HabitTools.cs` |
| Rate-limited endpoint | search `[DistributedRateLimit]` |
