# Chat AI Tools

This directory holds the chat agent's tool layer. Each tool is a small adapter that
exposes one capability to the AI through a JSON-schema'd contract and delegates to the
existing application logic (usually a MediatR command/query).

## The `IAiTool` contract

A tool implements `IAiTool` (`IAiTool.cs`):

- `string Name` — the stable tool name the model calls. It is also the agent
  operation id, so it must be unique across all tools.
- `string Description` — what the tool does and when to use it.
- `bool IsReadOnly` (default `false`) — read-only tools never mutate state; this drives
  the `IsAgentExecutable`/audit semantics in the catalog.
- `int Order` (default `int.MaxValue`) — execution priority within a single tool-calling
  iteration (see below).
- `object GetParameterSchema()` — the JSON schema for the tool's arguments. Build it from
  the `JsonSchemaTypes` constants to avoid duplicated string literals.
- `Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)` —
  validate arguments at the boundary, run the work, and return a `ToolResult`
  (`Success`, `EntityId`, `EntityName`, `Error`, `Payload`).

Argument parsing helpers live in `JsonArgumentParser` (same assembly, internal).

## Tool ordering (`Order`)

When the model requests several tool calls in one iteration, they are executed in
ascending `Order` (`ProcessUserChatCommand.ProcessToolCallsAsync`). The order is data on
the tool, not a hardcoded switch:

```
calls.OrderBy(c => registry.GetTool(c.Name)?.Order ?? int.MaxValue)
```

Reserved values:

| Order | Tool             | Why                                                   |
| ----- | ---------------- | ----------------------------------------------------- |
| 0     | `create_habit`   | Parent habits must exist before sub-habits/tags.      |
| 1     | `create_sub_habit` | Sub-habits depend on their parent.                  |
| 2     | `assign_tags`    | Tags attach to a habit that must already exist.       |

Every other tool keeps the default `int.MaxValue` and runs last. `OrderBy` is stable, so
tools that share an `Order` keep their original call order.

## Catalog mapping is mandatory

Every registered `IAiTool` MUST have a matching `chatTools` entry on an agent capability
in `AgentCatalogService`. At startup `BuildOperations` enumerates all registered tools and
throws `InvalidOperationException` if any tool name is not mapped to a capability. So a new
tool is always added together with its catalog `chatTools` entry and its DI registration in
`ServiceCollectionExtensions`.

## MCP routes through the same tools

The MCP server's mutating tools (`Orbit.Api/Mcp/Tools/*`) do not call MediatR directly.
They forward to `McpExecutorBridge` → `IAgentOperationExecutor`, which resolves the backing
`IAiTool` by name and the matching catalog operation, runs the same policy evaluation
(read-only-credential denial, ownership pre-check, confirmation gating) and writes an
`AgentAuditLogs` row. Chat and MCP therefore share one implementation and one policy surface
per capability; read-only MCP tools stay on MediatR.


## Typed habit query results

`query_habits` retains its formatted `EntityName` and also returns a `HabitListCard`
payload from the same filtered schedule result. Rows preserve order, hierarchy and
queried completion status. The optional `date` is the resolved user-local query day.
The scope is `date` for a historical or future schedule, `today` for the resolved
current day, and `all` for an undated query. An undated query retains its filters;
`all` does not mean the payload contains every habit.

The chat builder uses that payload for a single query. A failed query or multiple
habit queries cannot fall back to live context or select the last result for an
unqualified directive. Explicit query identity, history, paging, refresh and focused
period analytics remain part of thomasluizon/orbit-tickets#1616. The dated-row fix
alone does not complete that ticket.

Habit logs contain completion or skip markers. `Habit.Log` writes a value of one
and no measurement unit is stored. A marker sum cannot establish minutes, distance,
volume or another measured quantity; the measured-value portion of #1616 requires
an owner decision before its contract can be implemented.
