using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Common;
using Orbit.Application.Chat.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    internal sealed class ChatTurnState
    {
        public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
        public string? UserContent { get; set; }
        public bool Enabled { get; set; }
        public bool UsedModel { get; set; }
        public bool MessageAllowanceConsumed { get; set; }
        public List<ConversationToolCall> ToolCalls { get; } = [];
    }

    private void RecordConversationSafely(ProcessUserChatCommand request,
        ChatTurnState recording, Result<ChatResponse>? response)
    {
        if (!recording.Enabled || conversationRecorder is null)
            return;
        try
        {
            conversationRecorder.TryRecord(new ConversationTurnCapture(request.UserId, request.TurnId ?? Guid.NewGuid(),
                recording.CreatedAtUtc, recording.UserContent ?? request.Message, response is { IsSuccess: true } ? response.Value.AiMessage : null,
                JsonSerializer.Serialize(recording.ToolCalls), recording.UsedModel,
                response is { IsSuccess: true } ? JsonSerializer.Serialize(response.Value, ChatStreamEvent.SerializerOptions) : null));
        }
        catch (Exception)
        {
            logger.LogWarning("Conversation recording failed; the chat response is unaffected");
        }
    }
}
