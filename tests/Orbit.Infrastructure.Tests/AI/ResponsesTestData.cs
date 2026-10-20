#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using OpenAI.Responses;

namespace Orbit.Infrastructure.Tests.AI;

internal static class ResponsesTestData
{
    internal static ResponseResult Response(string? text = null, int inputTokens = 1, int outputTokens = 2, int cachedTokens = 0)
    {
        var response = new ResponseResult
        {
            Id = "resp_test",
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(1700000000),
            Model = "gpt-6-luna",
            Status = ResponseStatus.Completed,
            Usage = new ResponseTokenUsage
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
                TotalTokenCount = inputTokens + outputTokens,
                InputTokenDetails = new ResponseInputTokenUsageDetails { CachedTokenCount = cachedTokens }
            }
        };
        if (text is not null)
            response.OutputItems.Add(ResponseItem.CreateAssistantMessageItem(text));
        return response;
    }

    internal static string Text(string text, int inputTokens = 1, int outputTokens = 2, int cachedTokens = 0)
        => ModelReaderWriter.Write(Response(text, inputTokens, outputTokens, cachedTokens)).ToString();

    internal static string Event(StreamingResponseUpdate update)
        => $"data: {ModelReaderWriter.Write(update)}\n\n";

    internal static string Stream(ResponseResult response, params string[] deltas)
    {
        var events = string.Concat(deltas.Select(delta => Event(new StreamingResponseOutputTextDeltaUpdate
        {
            Delta = delta,
            ItemId = "msg_test",
            OutputIndex = 0,
            ContentIndex = 0
        })));
        return events + (response.Status == ResponseStatus.Incomplete
            ? Event(new StreamingResponseIncompleteUpdate { Response = response })
            : Event(new StreamingResponseCompletedUpdate { Response = response }));
    }
}
