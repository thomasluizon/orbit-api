using System.Text.Json;
using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public sealed class ConversationTurn : Entity
{
    public Guid UserId { get; private set; }
    public Guid TurnId { get; private set; }
    public string Role { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }
    public string? Model { get; private set; }
    public string? Content { get; private set; }
    public string ToolCallsJson { get; private set; } = "[]";
    public string? ResponseJson { get; private set; }
    public Guid? ConversationId { get; private set; }
    public long? TurnOrder { get; private set; }
    public string ToolResultsJson { get; private set; } = "[]";
    public string PreviewOutcomesJson { get; private set; } = "[]";
    public string? ImageObjectKey { get; private set; }
    public string? ImageContentType { get; private set; }
    public string? ImageSha256 { get; private set; }

    private ConversationTurn() { }

    public static Result<ConversationTurn> Create(Guid userId, Guid turnId, string role,
        DateTime createdAtUtc, string? model, string? content, string toolCallsJson, string? responseJson = null,
        Guid? conversationId = null, long? turnOrder = null, string toolResultsJson = "[]",
        string previewOutcomesJson = "[]", string? imageObjectKey = null, string? imageContentType = null,
        string? imageSha256 = null)
    {
        if (userId == Guid.Empty || turnId == Guid.Empty)
            return Result.Failure<ConversationTurn>("A user and turn identifier are required.");
        if (role is not ("user" or "assistant"))
            return Result.Failure<ConversationTurn>("Conversation role must be user or assistant.");
        if (createdAtUtc.Kind != DateTimeKind.Utc || createdAtUtc == default)
            return Result.Failure<ConversationTurn>("A UTC timestamp is required.");
        if (model is { Length: > 200 } || string.IsNullOrWhiteSpace(toolCallsJson))
            return Result.Failure<ConversationTurn>("Conversation text exceeds the recording limits.");
        if (role == "user" && string.IsNullOrWhiteSpace(content))
            return Result.Failure<ConversationTurn>("User content is required.");
        if (conversationId == Guid.Empty || conversationId.HasValue != turnOrder.HasValue || turnOrder is <= 0)
            return Result.Failure<ConversationTurn>("A conversation requires a positive turn order.");
        if (!IsValidImage(userId, conversationId, role, imageObjectKey, imageContentType, imageSha256))
            return Result.Failure<ConversationTurn>("Conversation image metadata is invalid.");
        try
        {
            using var document = JsonDocument.Parse(toolCallsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return Result.Failure<ConversationTurn>("Tool calls must be a JSON array.");
            foreach (var json in new[] { toolResultsJson, previewOutcomesJson })
            {
                using var array = JsonDocument.Parse(json);
                if (array.RootElement.ValueKind != JsonValueKind.Array)
                    return Result.Failure<ConversationTurn>("Conversation results must be JSON arrays.");
            }
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
            ResponseJson = responseJson,
            ConversationId = conversationId,
            TurnOrder = turnOrder,
            ToolResultsJson = toolResultsJson,
            PreviewOutcomesJson = previewOutcomesJson,
            ImageObjectKey = imageObjectKey,
            ImageContentType = imageContentType,
            ImageSha256 = imageSha256
        });
    }

    private static bool IsValidImage(Guid userId, Guid? conversationId, string role, string? key, string? mime, string? sha256)
    {
        if (key is null) return mime is null && sha256 is null;
        return role == "user" && conversationId is not null
            && key.StartsWith($"{userId}/conversations/{conversationId}/", StringComparison.Ordinal)
            && !key.Contains("..", StringComparison.Ordinal)
            && mime is "image/png" or "image/jpeg" or "image/webp"
            && sha256 is { Length: 64 } && sha256.All(char.IsAsciiHexDigit);
    }

    public void UpdatePreviewOutcome(Guid pendingOperationId, string outcome, JsonElement arguments, int revision)
    {
        if (pendingOperationId == Guid.Empty || outcome is not ("pending" or "approved" or "edited" or "rejected" or "expired")
            || arguments.ValueKind != JsonValueKind.Object || revision < 0)
            throw new ArgumentException("Conversation preview outcome is invalid.");
        var previews = JsonSerializer.Deserialize<List<ConversationPreviewOutcome>>(PreviewOutcomesJson) ?? [];
        var index = previews.FindIndex(preview => preview.PendingOperationId == pendingOperationId);
        if (index < 0) return;
        previews[index] = new(pendingOperationId, outcome, arguments, revision);
        var json = JsonSerializer.Serialize(previews);
        PreviewOutcomesJson = json;
    }
}

public sealed record ConversationPreviewOutcome(Guid PendingOperationId, string Outcome, JsonElement Arguments, int Revision);
