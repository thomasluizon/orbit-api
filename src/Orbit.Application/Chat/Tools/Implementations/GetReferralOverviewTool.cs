using System.Text.Json;
using MediatR;
using Orbit.Application.Referrals.Queries;

namespace Orbit.Application.Chat.Tools.Implementations;

public class GetReferralOverviewTool(IMediator mediator) : IAiTool
{
    public string Name => "get_referral_overview";
    public string Description => "Read the user's referral dashboard, code, link, and stats.";
    public bool IsReadOnly => true;

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new { }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var result = await mediator.Send(new GetReferralDashboardQuery(userId), ct);
        return result.IsSuccess
            ? new ToolResult(true, Payload: result.Value)
            : ToolResult.FromFailure(result);
    }
}
