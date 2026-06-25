using System.Text.Json;
using MediatR;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Profile.Commands;

namespace Orbit.Application.Chat.Tools.Implementations;

public class ManageAccountTool(IMediator mediator) : IAiTool
{
    public string Name => "manage_account";
    public string Description => "Reset the account, request an account deletion code, or confirm account deletion with a code.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            action = new { type = JsonSchemaTypes.String, @enum = new[] { "reset_account", "request_deletion", "confirm_deletion" } },
            code = new { type = JsonSchemaTypes.String, nullable = true }
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
            "reset_account" => await ResetAccountAsync(userId, ct),
            "request_deletion" => await RequestDeletionAsync(userId, ct),
            "confirm_deletion" => await ConfirmDeletionAsync(args, userId, ct),
            _ => new ToolResult(false, Error: $"Unsupported action '{action}'.")
        };
    }

    private async Task<ToolResult> ResetAccountAsync(Guid userId, CancellationToken ct)
    {
        var result = await mediator.Send(new ResetAccountCommand(userId), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Account reset completed", Payload: new { success = true })
            : ToolResult.FromFailure(result, userId.ToString());
    }

    private async Task<ToolResult> RequestDeletionAsync(Guid userId, CancellationToken ct)
    {
        var result = await mediator.Send(new RequestAccountDeletionCommand(userId), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Deletion code requested", Payload: new { success = true })
            : ToolResult.FromFailure(result, userId.ToString());
    }

    private async Task<ToolResult> ConfirmDeletionAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var code = JsonArgumentParser.GetOptionalString(args, "code");
        if (string.IsNullOrWhiteSpace(code))
            return new ToolResult(false, Error: "code is required.");

        var result = await mediator.Send(new ConfirmAccountDeletionCommand(userId, code), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Account deletion confirmed", Payload: new { scheduledDeletionAt = result.Value })
            : ToolResult.FromFailure(result, userId.ToString());
    }
}
