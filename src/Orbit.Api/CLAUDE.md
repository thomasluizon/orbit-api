# Orbit.Api

The HTTP boundary. A controller sends a MediatR command or query and returns the result. It holds no business logic.

## Authorization

The fallback policy requires an authenticated user on each endpoint. Mark a public endpoint with `[AllowAnonymous]` or `.AllowAnonymous()`. `/mcp` is public in the router and `McpHttpGate` authenticates each request.

## Security

- CORS allows only the listed methods and headers. Never call `AllowAnyHeader()` or `AllowAnyMethod()`.
- Reject a Stripe webhook when `WebhookSecret` is not set.
- Put `[DistributedRateLimit]` on an endpoint that a client can abuse.

## API versioning

A controller supports v1 and v2 through the `api-version` request header. A request without the header gets v1. The documents in [`OpenApi/artifacts.json`](OpenApi/artifacts.json) are the published OpenAPI revisions. [`OpenApi/README.md`](OpenApi/README.md) explains how to select, generate and publish them.

To make a breaking change, add the new shape under v2 with its own DTOs and `[MapToApiVersion]` actions. Keep v1. Shared handlers and DTOs must work with both versions. Retire v1 only when every live client uses v2. Enforce this with the `MinSupportedVersion` gate. The raise procedure is in [`infra/minimum-supported-version.md`](../../infra/minimum-supported-version.md).

## MCP

`Mcp/Tools/*Tools.cs` exposes MediatR operations as MCP tools. Send a mutation through `McpExecutorBridge` to `IAgentOperationExecutor`. This gives the mutation the policy checks and an `AgentAuditLogs` row. Use `mediator.Send` in an MCP tool only for a `*Query`. The architecture test `McpToolsDoNotDispatchCommands` fails on a `*Command`. A mutation wrapper sends the backing chat tool name as `operationId` and a snake_case argument object. A destructive wrapper also sends the `confirmationToken` of the caller.
