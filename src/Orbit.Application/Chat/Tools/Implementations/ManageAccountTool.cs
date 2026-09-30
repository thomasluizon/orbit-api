using System.Text.Json;
using MediatR;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Profile.Commands;

namespace Orbit.Application.Chat.Tools.Implementations;

public class ManageAccountTool(IMediator mediator) : IAiTool, IArgumentCheckTool
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
            "reset_account" => await ResetAccountAsync(userId, ct, checkOnly),
            "request_deletion" => await RequestDeletionAsync(userId, ct, checkOnly),
            "confirm_deletion" => await ConfirmDeletionAsync(args, userId, ct, checkOnly),
            _ => new ToolResult(false, Error: $"Unsupported action '{action}'.")
        };
    }

    private async Task<ToolResult> ResetAccountAsync(Guid userId, CancellationToken ct, bool checkOnly)
    {
        var command = new ResetAccountCommand(userId);
        if (checkOnly)
            return await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);

        var result = await mediator.Send(command, ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Account reset completed", Payload: new { success = true })
            : ToolResult.FromFailure(result, userId.ToString());
    }

    private async Task<ToolResult> RequestDeletionAsync(Guid userId, CancellationToken ct, bool checkOnly)
    {
        var command = new RequestAccountDeletionCommand(userId);
        if (checkOnly)
            return await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);

        var result = await mediator.Send(command, ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Deletion code requested", Payload: new { success = true })
            : ToolResult.FromFailure(result, userId.ToString());
    }

    private async Task<ToolResult> ConfirmDeletionAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        var code = JsonArgumentParser.GetOptionalString(args, "code");
        if (string.IsNullOrWhiteSpace(code))
            return new ToolResult(false, Error: "code is required.");

        var command = new ConfirmAccountDeletionCommand(userId, code);
        if (checkOnly)
            return await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);

        var result = await mediator.Send(command, ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Account deletion confirmed", Payload: new { scheduledDeletionAt = result.Value })
            : ToolResult.FromFailure(result, userId.ToString());
    }
}
