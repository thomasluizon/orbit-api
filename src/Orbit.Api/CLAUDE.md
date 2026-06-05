# Orbit.Api — HTTP boundary

Controllers, middleware, OpenAPI/Scalar, DI config, `Program.cs`. This project should be thin: it deserializes requests, dispatches to MediatR, and serializes the result.

## Controllers

- One controller per resource: `HabitsController`, `NotificationController`, `AuthController`, `ProfileController`, `TagsController`, `ChatController`, etc. (already exist — match their shape.)
- Inject `ISender` from MediatR. Don't inject repositories or services directly.
- Inject `ILogger<TController>` and log business events at info level.
- Action method = thin pass-through to `_sender.Send(command/query)`. No business logic.
- Use `result.ToPayGateAwareResult(v => Ok(v))` from `Extensions/ResultActionResultExtensions.cs` — never hand-write the 403/PAY_GATE response block.

## Authorization

Default to `[Authorize]` at the controller class level. Override with `[AllowAnonymous]` on individual actions when truly public (e.g., `POST /api/auth/send-code`). The middleware pipeline rejects unauthenticated requests for `[Authorize]` endpoints with 401.

Exempt by construction:
- `GET /health` (HealthCheckController)
- `POST /api/auth/send-code`, `verify-code`, `google` (AuthController)
- Stripe webhook (verified by signature, not JWT)

## Request validation

FluentValidation runs via the MediatR validation pipeline. Controllers do NOT call validators directly. If validation fails, `ValidationExceptionHandler` middleware converts it to a 400 with field errors.

## OpenAPI / Scalar

Dev-only. `BearerSecuritySchemeTransformer` adds the JWT bearer scheme. New endpoints show up automatically — annotate with `[ProducesResponseType(StatusCodes.Status200OK)]` etc. to document response shapes.

## Middleware (already wired in Program.cs)

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
