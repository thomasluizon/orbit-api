using System.Text.Json;
using FluentValidation;
using Orbit.Domain.Entities;

namespace Orbit.Application.Chat.Validators;

public sealed class ConversationTurnCaptureValidator : AbstractValidator<ConversationTurnCapture>
{
    public ConversationTurnCaptureValidator()
    {
        RuleFor(turn => turn.UserId).NotEmpty();
        RuleFor(turn => turn.TurnId).NotEmpty();
        RuleFor(turn => turn.CreatedAtUtc).Must(value => value != default && value.Kind == DateTimeKind.Utc);
        RuleFor(turn => turn.UserContent).NotEmpty();
        RuleFor(turn => turn.ToolCallsJson).NotEmpty().Must(IsArray);
        RuleFor(turn => turn).Must(turn => (long)(turn.UserContent?.Length ?? 0)
            + (turn.AssistantContent?.Length ?? 0) + (turn.ToolCallsJson?.Length ?? 0)
            <= ConversationTurn.MaxTextLength).WithMessage("Conversation exceeds the recording limit.");
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
