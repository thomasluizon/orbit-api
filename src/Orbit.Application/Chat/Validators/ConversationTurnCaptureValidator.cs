using System.Text.Json;
using FluentValidation;

namespace Orbit.Application.Chat.Validators;

public sealed class ConversationTurnCaptureValidator : AbstractValidator<ConversationTurnCapture>
{
    public ConversationTurnCaptureValidator()
    {
        RuleFor(turn => turn.ConversationId).Must(id => id is null || id != Guid.Empty);
        RuleFor(turn => turn.ImageMimeType).Must(mime => mime is null or "image/png" or "image/jpeg" or "image/webp");
        RuleFor(turn => turn).Must(turn => turn.ImageData is null && turn.ImageMimeType is null
            || turn.ImageData is { Length: > 0 and <= 10_485_760 } && turn.ImageMimeType is not null);
        RuleFor(turn => turn.UserId).NotEmpty();
        RuleFor(turn => turn.TurnId).NotEmpty();
        RuleFor(turn => turn.CreatedAtUtc).Must(value => value != default && value.Kind == DateTimeKind.Utc);
        RuleFor(turn => turn.UserContent).NotEmpty();
        RuleFor(turn => turn.ToolCallsJson).NotEmpty().Must(IsArray);
        RuleFor(turn => turn.ResponseJson).Must(IsResponseObject);
    }

    private static bool IsResponseObject(string? json)
    {
        if (json is null) return true;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsArray(string? json)
    {
        if (json is null)
            return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
