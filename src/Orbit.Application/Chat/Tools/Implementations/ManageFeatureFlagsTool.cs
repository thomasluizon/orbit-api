using System.Text.Json;
using MediatR;
using Orbit.Application.FeatureFlags;
using Orbit.Application.FeatureFlags.Commands;

namespace Orbit.Application.Chat.Tools.Implementations;

public class ManageFeatureFlagsTool(IMediator mediator) : IAiTool, IMcpOnlyAiTool
{
    public string Name => FeatureFlagActions.ToolName;
    public string Description => "Manage feature flags for accounts or everyone. Requires an administrator.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            action = new { type = JsonSchemaTypes.String, @enum = FeatureFlagActions.Writes },
            key = new { type = JsonSchemaTypes.String },
            emails = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.String }, nullable = true },
            description = new { type = JsonSchemaTypes.String, nullable = true }
        },
        required = new[] { "action", "key" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var result = await mediator.Send(new ManageFeatureFlagsCommand(userId,
            JsonArgumentParser.GetOptionalString(args, "action") ?? string.Empty,
            JsonArgumentParser.GetOptionalString(args, "key") ?? string.Empty,
            JsonArgumentParser.ParseStringArray(args, "emails"),
            JsonArgumentParser.GetOptionalString(args, "description")), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: result.Value.Key, Payload: result.Value)
            : ToolResult.FromFailure(result);
    }
}
