using System.Text.Json;
using MediatR;
using Orbit.Application.Support.Commands;

namespace Orbit.Application.Chat.Tools.Implementations;

public class SendSupportRequestTool(IMediator mediator) : IAiTool, IArgumentCheckTool
{
    public string Name => "send_support_request";
    public string Description => "Send a support request on behalf of the user.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            subject = new { type = JsonSchemaTypes.String },
            message = new { type = JsonSchemaTypes.String }
        },
        required = new[] { "subject", "message" }
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ExecuteCoreAsync(args, userId, ct, checkOnly: false);

    public Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ChatToolArgumentCheck.CheckAsync(this, args, () => ExecuteCoreAsync(args, userId, ct, checkOnly: true));

    private async Task<ToolResult> ExecuteCoreAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        var subject = JsonArgumentParser.GetOptionalString(args, "subject");
        var message = JsonArgumentParser.GetOptionalString(args, "message");

        if (string.IsNullOrWhiteSpace(subject) ||
            string.IsNullOrWhiteSpace(message))
        {
            return new ToolResult(false, Error: "subject and message are required.");
        }

        var command = new SendSupportCommand(userId, "", "", subject, message);
        if (checkOnly)
            return await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);

        var result = await mediator.Send(command, ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Support request sent", Payload: new { subject })
            : ToolResult.FromFailure(result, userId.ToString());
    }
}
