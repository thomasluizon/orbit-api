using System.ClientModel.Primitives;
using OpenAI.Chat;
using Orbit.Application.Common;

namespace Orbit.Infrastructure.AI;

/// <summary>
/// Reserves one token per serialized UTF-8 byte, message framing, and the full output allowance.
/// Byte-level tokenization cannot exceed the byte count for text; JSON and framing overcount input.
/// Media retains the request-wide worst-case reservation because encoded bytes do not bound vision tokens.
/// </summary>
internal static class AiTokenReservation
{
    internal const int DefaultMaxOutputTokens = 8192;

    internal static long Estimate(IReadOnlyList<ChatMessage> messages, ChatCompletionOptions options)
    {
        options.MaxOutputTokenCount ??= DefaultMaxOutputTokens;
        var tokens = checked(1024L + options.MaxOutputTokenCount.Value
            + ModelReaderWriter.Write(options).ToMemory().Length);
        foreach (var message in messages)
        {
            tokens = checked(tokens + 64 + ModelReaderWriter.Write(message).ToMemory().Length);
            foreach (var part in message.Content)
                if (part.Kind != ChatMessageContentPartKind.Text && part.Kind != ChatMessageContentPartKind.Refusal)
                    tokens = checked(tokens + AppConstants.MaxAiRequestTotalTokens);
        }
        return tokens;
    }
}
