using System.Text.Json;
using MediatR;
using Orbit.Application.Referrals.Commands;

namespace Orbit.Application.Chat.Tools.Implementations;

public class GetReferralCodeTool(IMediator mediator) : IAiTool, IArgumentCheckTool
{
    public string Name => "get_referral_code";
    public string Description => "Get or create the user's referral code (generates one if absent).";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new { }
    };

    public Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        Task.FromResult(userId == Guid.Empty ? Orbit.Domain.Common.Result.Failure("User is required.") : Orbit.Domain.Common.Result.Success());

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var result = await mediator.Send(new GetOrCreateReferralCodeCommand(userId), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: result.Value, Payload: new { code = result.Value })
            : ToolResult.FromFailure(result);
    }
}
