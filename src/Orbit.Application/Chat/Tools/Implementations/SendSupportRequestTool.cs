using System.Text.Json;
using MediatR;
using Orbit.Application.Support.Commands;

namespace Orbit.Application.Chat.Tools.Implementations;

public class SendSupportRequestTool(IMediator mediator) : IAiTool
{
    public string Name => "send_support_request";
    public string Description => "Send a support request on behalf of the user.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            name = new { type = JsonSchemaTypes.String },
            email = new { type = JsonSchemaTypes.String },
            subject = new { type = JsonSchemaTypes.String },
            message = new { type = JsonSchemaTypes.String }
        },
        required = new[] { "name", "email", "subject", "message" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var name = JsonArgumentParser.GetOptionalString(args, "name");
        var email = JsonArgumentParser.GetOptionalString(args, "email");
        var subject = JsonArgumentParser.GetOptionalString(args, "subject");
        var message = JsonArgumentParser.GetOptionalString(args, "message");

        if (string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(email) ||
            string.IsNullOrWhiteSpace(subject) ||
            string.IsNullOrWhiteSpace(message))
        {
            return new ToolResult(false, Error: "name, email, subject, and message are required.");
        }

        var result = await mediator.Send(new SendSupportCommand(userId, name, email, subject, message), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Support request sent", Payload: new { subject })
            : ToolResult.FromFailure(result, userId.ToString());
    }
}
