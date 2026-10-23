using System.Text.Json;
using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public sealed class ConversationTurn : Entity
{
    public const int RetentionDays = 30;
    public const int MaxTextLength = 65_536;
    public Guid UserId { get; private set; }
    public Guid TurnId { get; private set; }
    public string Role { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }
    public string? Model { get; private set; }
    public string? Content { get; private set; }
    public string ToolCallsJson { get; private set; } = "[]";
    public string? ResponseJson { get; private set; }

    private ConversationTurn() { }

    public static Result<ConversationTurn> Create(Guid userId, Guid turnId, string role,
        DateTime createdAtUtc, string? model, string? content, string toolCallsJson, string? responseJson = null)
    {
        if (userId == Guid.Empty || turnId == Guid.Empty)
            return Result.Failure<ConversationTurn>("A user and turn identifier are required.");
        if (role is not ("user" or "assistant"))
            return Result.Failure<ConversationTurn>("Conversation role must be user or assistant.");
        if (createdAtUtc.Kind != DateTimeKind.Utc || createdAtUtc == default)
            return Result.Failure<ConversationTurn>("A UTC timestamp is required.");
        if (model is { Length: > 200 } || content is { Length: > MaxTextLength }
            || string.IsNullOrWhiteSpace(toolCallsJson) || toolCallsJson.Length > MaxTextLength
            || responseJson is { Length: > MaxTextLength })
            return Result.Failure<ConversationTurn>("Conversation text exceeds the recording limits.");
        if (role == "user" && string.IsNullOrWhiteSpace(content))
            return Result.Failure<ConversationTurn>("User content is required.");
        try
        {
            using var document = JsonDocument.Parse(toolCallsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return Result.Failure<ConversationTurn>("Tool calls must be a JSON array.");
            if (responseJson is not null)
            {
                using var response = JsonDocument.Parse(responseJson);
                if (role != "assistant" || response.RootElement.ValueKind != JsonValueKind.Object)
                    return Result.Failure<ConversationTurn>("Only assistant turns can store a response object.");
            }
        }
        catch (JsonException)
        {
            return Result.Failure<ConversationTurn>("Tool calls must be valid JSON.");
        }
        return Result.Success(new ConversationTurn
        {
            UserId = userId,
            TurnId = turnId,
            Role = role,
            CreatedAtUtc = createdAtUtc,
            Model = model,
            Content = content,
            ToolCallsJson = toolCallsJson,
            ResponseJson = responseJson
        });
    }
}
