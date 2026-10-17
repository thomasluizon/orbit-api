using System.Globalization;
using System.Text.Json;
using MediatR;
using Orbit.Application.ApiKeys.Commands;

namespace Orbit.Application.Chat.Tools.Implementations;

public class ManageApiKeysTool(IMediator mediator) : IAiTool, IArgumentCheckTool
{
    public string Name => "manage_api_keys";
    public string Description => "Create or revoke scoped API keys.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            action = new { type = JsonSchemaTypes.String, @enum = new[] { "create", "revoke" } },
            key_id = new { type = JsonSchemaTypes.String, nullable = true },
            name = new { type = JsonSchemaTypes.String, nullable = true },
            scopes = new
            {
                type = JsonSchemaTypes.Array,
                nullable = true,
                items = new { type = JsonSchemaTypes.String }
            },
            is_read_only = new { type = JsonSchemaTypes.Boolean, nullable = true },
            expires_at_utc = new { type = JsonSchemaTypes.String, nullable = true, description = "ISO-8601 UTC timestamp." }
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
            "create" => await CreateAsync(args, userId, ct, checkOnly),
            "revoke" => await RevokeAsync(args, userId, ct, checkOnly),
            _ => new ToolResult(false, Error: $"Unsupported action '{action}'.")
        };
    }

    private async Task<ToolResult> CreateAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        var name = JsonArgumentParser.GetOptionalString(args, "name");
        if (string.IsNullOrWhiteSpace(name))
            return new ToolResult(false, Error: "name is required.");

        var scopes = JsonArgumentParser.ParseStringArray(args, "scopes");
        var isReadOnly = JsonArgumentParser.GetOptionalBool(args, "is_read_only") ?? false;
        var expiresAtValue = JsonArgumentParser.GetOptionalString(args, "expires_at_utc");
        DateTime? expiresAtUtc = null;
        if (JsonArgumentParser.PropertyExists(args, "expires_at_utc") &&
            !TryParseUtcTimestamp(expiresAtValue, out expiresAtUtc))
        {
            return new ToolResult(false, Error: "expires_at_utc must be a valid ISO-8601 UTC timestamp.");
        }

        var command = new CreateApiKeyCommand(userId, name, scopes, isReadOnly, expiresAtUtc);
        if (checkOnly)
            return await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);

        var result = await mediator.Send(command, ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: result.Value.Id.ToString(), EntityName: result.Value.Name, Payload: result.Value)
            : ToolResult.FromFailure(result, userId.ToString());
    }

    private async Task<ToolResult> RevokeAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        var keyId = JsonArgumentParser.GetOptionalString(args, "key_id");
        if (!Guid.TryParse(keyId, out var parsedId))
            return new ToolResult(false, Error: "key_id must be a valid GUID.");

        var command = new RevokeApiKeyCommand(userId, parsedId);
        if (checkOnly)
            return await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);

        var result = await mediator.Send(command, ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: parsedId.ToString(), EntityName: "Revoked API key", Payload: new { id = parsedId })
            : ToolResult.FromFailure(result, parsedId.ToString());
    }

    private static bool TryParseUtcTimestamp(string? value, out DateTime? parsedUtc)
    {
        parsedUtc = null;

        if (string.IsNullOrWhiteSpace(value))
            return true;

        if (!DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
        )
        {
            return false;
        }

        parsedUtc = parsed;
        return true;
    }
}
