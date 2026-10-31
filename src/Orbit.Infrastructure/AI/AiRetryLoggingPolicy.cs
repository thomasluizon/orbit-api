using System.ClientModel.Primitives;
using Microsoft.Extensions.Logging;

namespace Orbit.Infrastructure.AI;

public partial class AiRetryLoggingPolicy(int maxRetries, ILogger logger) : ClientRetryPolicy(maxRetries)
{
    private sealed class AttemptCounter
    {
        public int Value;
    }

    protected override bool ShouldRetry(PipelineMessage message, Exception? exception)
    {
        if (AiProviderErrors.IsQuotaCandidate(message.Response))
            message.Response.BufferContent(message.CancellationToken);
        return EvaluateRetry(message, exception);
    }

    protected override async ValueTask<bool> ShouldRetryAsync(PipelineMessage message, Exception? exception)
    {
        if (AiProviderErrors.IsQuotaCandidate(message.Response))
            await message.Response.BufferContentAsync(message.CancellationToken);
        return EvaluateRetry(message, exception);
    }

    private bool EvaluateRetry(PipelineMessage message, Exception? exception)
    {
        var retriable = !AiProviderErrors.IsQuotaExhausted(message.Response)
            && base.ShouldRetry(message, exception);
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
