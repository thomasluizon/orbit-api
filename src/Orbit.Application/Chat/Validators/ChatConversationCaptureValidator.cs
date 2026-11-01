using FluentValidation;
using Orbit.Application.Common;

namespace Orbit.Application.Chat.Validators;

public sealed class ChatConversationCaptureValidator : AbstractValidator<ChatConversationCapture>
{
    public ChatConversationCaptureValidator()
    {
        RuleFor(turn => turn.UserId).NotEmpty();
        RuleFor(turn => turn.ConversationId).NotEmpty();
        RuleFor(turn => turn.TurnId).NotEmpty();
        RuleFor(turn => turn.CreatedAtUtc).Must(value => value != default && value.Kind == DateTimeKind.Utc);
        RuleFor(turn => turn.UserMessage).NotEmpty().MaximumLength(AppConstants.MaxChatMessageLength);
        RuleFor(turn => turn.ImageData).Must(data => data is null || data.Length <= 20_971_520);
        RuleFor(turn => turn.ImageMimeType).Must((turn, mime) => turn.ImageData is null
            || mime is "image/png" or "image/jpeg" or "image/webp" or "image/gif");
    }
}
