using System.Text.Json;
using MediatR;
using Orbit.Application.ApiKeys.Queries;

namespace Orbit.Application.Chat.Tools.Implementations;

public class GetApiKeysTool(IMediator mediator) : IAiTool
{
    public string Name => "get_api_keys";
    public string Description => "Read the user's API keys, scopes, last use, and revocation state.";
    public bool IsReadOnly => true;

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new { }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var result = await mediator.Send(new GetApiKeysQuery(userId), ct);
        return result.IsSuccess
            ? new ToolResult(true, Payload: result.Value)
            : ToolResult.FromFailure(result);
    }
}
