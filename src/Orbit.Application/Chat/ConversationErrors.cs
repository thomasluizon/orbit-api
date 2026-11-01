using Orbit.Application.Common;
using Orbit.Domain.Common;

namespace Orbit.Application.Chat;

public static class ConversationErrors
{
    public const string NotFoundCode = "CONVERSATION_NOT_FOUND";
    public static AppError NotFound => new(NotFoundCode, ErrorCopy.English(NotFoundCode));
}
