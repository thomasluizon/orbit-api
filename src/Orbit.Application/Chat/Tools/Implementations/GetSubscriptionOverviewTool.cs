using System.Text.Json;
using MediatR;
using Orbit.Application.Subscriptions.Queries;

namespace Orbit.Application.Chat.Tools.Implementations;

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

        object? status = null;
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
            if (billingResult.IsFailure) return ToolResult.FromFailure(billingResult);
            billing = billingResult.Value;
        }

        if (includePlans)
        {
            var plansResult = await mediator.Send(new GetPlansQuery(userId, null, null), ct);
            if (plansResult.IsFailure) return ToolResult.FromFailure(plansResult);
            plans = plansResult.Value;
        }

        return new ToolResult(true, Payload: new { status, billing, plans });
    }
}
