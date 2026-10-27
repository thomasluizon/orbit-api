using System.Text.Json;
using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Subscriptions.Queries;
using Orbit.Application.Subscriptions;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed record SubscriptionOverviewPayload(
    [property: System.Text.Json.Serialization.JsonPropertyName("status")] SubscriptionStatusResponse? Status,
    [property: System.Text.Json.Serialization.JsonPropertyName("billing")] object? Billing,
    [property: System.Text.Json.Serialization.JsonPropertyName("plans")] object? Plans);

public class GetSubscriptionOverviewTool(IMediator mediator) : IAiTool
{
    public string Name => "get_subscription_overview";
    public string Description => "Read subscription status, billing details, and available plans.";
    public bool IsReadOnly => true;

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            include_status = new { type = JsonSchemaTypes.Boolean },
            include_billing = new { type = JsonSchemaTypes.Boolean },
            include_plans = new { type = JsonSchemaTypes.Boolean }
        }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var includeStatus = JsonArgumentParser.GetOptionalBool(args, "include_status") ?? true;
        var includeBilling = JsonArgumentParser.GetOptionalBool(args, "include_billing") ?? true;
        var includePlans = JsonArgumentParser.GetOptionalBool(args, "include_plans") ?? true;

        SubscriptionStatusResponse? status = null;
        object? billing = null;
        object? plans = null;

        if (includeStatus)
        {
            var statusResult = await mediator.Send(new GetSubscriptionStatusQuery(userId), ct);
            if (statusResult.IsFailure) return ToolResult.FromFailure(statusResult);
            status = statusResult.Value;
        }

        if (includeBilling)
        {
            var billingResult = await mediator.Send(new GetBillingDetailsQuery(userId), ct);
            if (billingResult.IsFailure && billingResult.ErrorCode is not (ErrorCodes.NoActiveSubscription or ErrorCodes.SubscriptionNotFound))
                return ToolResult.FromFailure(billingResult);
            billing = billingResult.IsSuccess ? billingResult.Value : null;
        }

        if (includePlans)
        {
            var plansResult = await mediator.Send(new GetPlansQuery(userId, null, null), ct);
            if (plansResult.IsFailure) return ToolResult.FromFailure(plansResult);
            plans = plansResult.Value;
        }

        return new ToolResult(true, Payload: new SubscriptionOverviewPayload(status, billing, plans));
    }
}
