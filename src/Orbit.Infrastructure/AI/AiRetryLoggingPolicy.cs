using System.ClientModel.Primitives;
using Microsoft.Extensions.Logging;

namespace Orbit.Infrastructure.AI;

/// <summary>
/// Retry policy for AI completions that logs every failed attempt with its
/// attempt number and retriability. The OpenAI SDK retries silently by default,
/// which let a hung connection burn the full network timeout unobserved; this
/// policy makes each attempt visible so latency tails are diagnosable from
/// production logs.
/// </summary>
public partial class AiRetryLoggingPolicy(int maxRetries, ILogger logger) : ClientRetryPolicy(maxRetries)
{
    private sealed class AttemptCounter
    {
        public int Value;
    }

    // WHY: ShouldRetryAsync is not overridden — its base dispatches to this sync virtual, so overriding both double-logs every attempt: https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/core/System.ClientModel/src/Pipeline/ClientRetryPolicy.cs
    protected override bool ShouldRetry(PipelineMessage message, Exception? exception)
    {
        var retriable = base.ShouldRetry(message, exception);
        LogFailedAttempt(message, exception, retriable);
        return retriable;
    }

    private void LogFailedAttempt(PipelineMessage message, Exception? exception, bool retriable)
    {
        var response = message.Response;
        var attemptFailed = exception is not null || (response?.IsError ?? false);
        if (!attemptFailed)
            return;

        if (!message.TryGetProperty(typeof(AttemptCounter), out var existing) || existing is not AttemptCounter counter)
        {
            counter = new AttemptCounter();
            message.SetProperty(typeof(AttemptCounter), counter);
        }
        counter.Value++;

        var failure = exception?.GetType().Name ?? $"HTTP {response!.Status}";
        LogAiAttemptFailed(logger, counter.Value, failure, retriable);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "AI attempt {Attempt} failed ({Failure}); retriable: {Retriable}")]
    private static partial void LogAiAttemptFailed(ILogger logger, int attempt, string failure, bool retriable);
}
