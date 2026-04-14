using System.Text.Json;
using MediatR;
using Orbit.Application.Chat.Tools;
using Orbit.Application.UserFacts.Commands;
using Orbit.Application.UserFacts.Queries;

namespace Orbit.Application.Chat.Tools.Implementations;

public class GetUserFactsTool(IMediator mediator) : IAiTool
{
    public string Name => "get_user_facts";
    public string Description => "Read the AI memory facts currently stored for the user.";
    public bool IsReadOnly => true;

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new { }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var result = await mediator.Send(new GetUserFactsQuery(userId), ct);
        return result.IsSuccess
            ? new ToolResult(true, Payload: result.Value)
            : new ToolResult(false, Error: result.Error);
    }
}

public class DeleteUserFactsTool(IMediator mediator) : IAiTool
{
    public string Name => "delete_user_facts";
    public string Description => "Delete one user fact or multiple user facts by ID.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            fact_id = new { type = JsonSchemaTypes.String, nullable = true },
            fact_ids = new
            {
                type = JsonSchemaTypes.Array,
                nullable = true,
                items = new { type = JsonSchemaTypes.String }
            }
        }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var factIds = JsonArgumentParser.ParseGuidArray(args, "fact_ids");
        if (factIds is { Count: > 0 })
        {
            var bulkResult = await mediator.Send(new BulkDeleteUserFactsCommand(userId, factIds), ct);
            return bulkResult.IsSuccess
                ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Deleted user facts", Payload: new { deleted = bulkResult.Value, factIds })
                : new ToolResult(false, EntityId: userId.ToString(), Error: bulkResult.Error);
        }

        var factId = JsonArgumentParser.GetOptionalString(args, "fact_id");
        if (!Guid.TryParse(factId, out var parsedId))
            return new ToolResult(false, Error: "fact_id or fact_ids is required.");

        var result = await mediator.Send(new DeleteUserFactCommand(userId, parsedId), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: parsedId.ToString(), EntityName: "Deleted user fact", Payload: new { id = parsedId })
            : new ToolResult(false, EntityId: parsedId.ToString(), Error: result.Error);
    }
}
