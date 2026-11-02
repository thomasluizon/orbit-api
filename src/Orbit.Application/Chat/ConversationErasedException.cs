namespace Orbit.Application.Chat;

public sealed class ConversationErasedException() : Exception(ConversationErrors.NotFound.Message);
