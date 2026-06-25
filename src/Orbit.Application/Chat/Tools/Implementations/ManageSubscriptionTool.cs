using System.Text.Json;
using MediatR;
using Orbit.Application.Subscriptions.Commands;

namespace Orbit.Application.Chat.Tools.Implementations;

public class ManageSubscriptionTool(IMediator mediator) : IAiTool
{
    public string Name => "manage_subscription";
    public string Description => "Create a checkout session, create a billing portal session, or claim an ad reward.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            action = new { type = JsonSchemaTypes.String, @enum = new[] { "create_checkout", "create_portal", "claim_ad_reward" } },
            interval = new { type = JsonSchemaTypes.String, nullable = true, @enum = new[] { "monthly", "yearly" } }
        },
        required = new[] { "action" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var action = JsonArgumentParser.GetOptionalString(args, "action");
        if (string.IsNullOrWhiteSpace(action))
            return new ToolResult(false, Error: "action is required.");

        return action switch
        {
            "create_checkout" => await CreateCheckoutAsync(args, userId, ct),
            "create_portal" => await CreatePortalAsync(userId, ct),
            "claim_ad_reward" => await ClaimAdRewardAsync(userId, ct),
            _ => new ToolResult(false, Error: $"Unsupported action '{action}'.")
        };
    }

    private async Task<ToolResult> CreateCheckoutAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var interval = JsonArgumentParser.GetOptionalString(args, "interval");
        if (string.IsNullOrWhiteSpace(interval))
            return new ToolResult(false, Error: "interval is required.");

        var result = await mediator.Send(new CreateCheckoutCommand(userId, interval, null, null), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Created checkout session", Payload: result.Value)
            : ToolResult.FromFailure(result, userId.ToString());
    }

    private async Task<ToolResult> CreatePortalAsync(Guid userId, CancellationToken ct)
    {
        var result = await mediator.Send(new CreatePortalSessionCommand(userId), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Created billing portal session", Payload: result.Value)
            : ToolResult.FromFailure(result, userId.ToString());
    }

    private async Task<ToolResult> ClaimAdRewardAsync(Guid userId, CancellationToken ct)
    {
        var result = await mediator.Send(new ClaimAdRewardCommand(userId), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Claimed ad reward", Payload: result.Value)
            : ToolResult.FromFailure(result, userId.ToString());
    }
}
