using System.Text.Json;
using MediatR;
using Orbit.Application.Subscriptions.Commands;

namespace Orbit.Application.Chat.Tools.Implementations;

public class ManageSubscriptionTool(IMediator mediator) : IAiTool, IArgumentCheckTool
{
    public string Name => "manage_subscription";
    public string Description => "Create a checkout session or create a billing portal session.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            action = new { type = JsonSchemaTypes.String, @enum = new[] { "create_checkout", "create_portal" } },
            interval = new { type = JsonSchemaTypes.String, nullable = true, @enum = new[] { "monthly", "yearly" } }
        },
        required = new[] { "action" }
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ExecuteCoreAsync(args, userId, ct, checkOnly: false);

    public Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ChatToolArgumentCheck.CheckAsync(this, args, () => ExecuteCoreAsync(args, userId, ct, checkOnly: true));

    private async Task<ToolResult> ExecuteCoreAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        var action = JsonArgumentParser.GetOptionalString(args, "action");
        if (string.IsNullOrWhiteSpace(action))
            return new ToolResult(false, Error: "action is required.");

        return action switch
        {
            "create_checkout" => await CreateCheckoutAsync(args, userId, ct, checkOnly),
            "create_portal" => await CreatePortalAsync(userId, ct, checkOnly),
            _ => new ToolResult(false, Error: $"Unsupported action '{action}'.")
        };
    }

    private async Task<ToolResult> CreateCheckoutAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        var interval = JsonArgumentParser.GetOptionalString(args, "interval");
        if (string.IsNullOrWhiteSpace(interval))
            return new ToolResult(false, Error: "interval is required.");

        var command = new CreateCheckoutCommand(userId, interval, null, null);
        if (checkOnly)
            return await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);

        var result = await mediator.Send(command, ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Created checkout session", Payload: result.Value)
            : ToolResult.FromFailure(result, userId.ToString());
    }

    private async Task<ToolResult> CreatePortalAsync(Guid userId, CancellationToken ct, bool checkOnly)
    {
        var command = new CreatePortalSessionCommand(userId);
        if (checkOnly)
            return await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);

        var result = await mediator.Send(command, ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Created billing portal session", Payload: result.Value)
            : ToolResult.FromFailure(result, userId.ToString());
    }

}
