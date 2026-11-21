using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.ProactiveProbe;

/// <summary>Takes the place of the daily budget ledger. The probe has its own cost ceiling, so it keeps no ledger.</summary>
internal sealed class NoBudgetLedger : IAiDailyBudget
{
    public Task<Guid> ReserveAsync(Guid userId, long reservedMicroDollars, CancellationToken cancellationToken) => Task.FromResult(Guid.NewGuid());

    public Task SettleAsync(Guid callId, long microDollars, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Keeps the token counts that the real client reports for the call, in place of the usage aggregate.</summary>
internal sealed class TokenUsageLedger : IAiUsageRecorder
{
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }

    public void Reset()
    {
        InputTokens = 0;
        OutputTokens = 0;
    }

    public Task RecordAsync(string purpose, string model, long cachedTokens, long promptTokens, long completionTokens,
        long totalTokens, CancellationToken cancellationToken = default, Guid? userId = null, AiCallTelemetry? telemetry = null,
        long cacheWriteTokens = 0, long reasoningTokens = 0)
    {
        InputTokens += promptTokens;
        OutputTokens += completionTokens;
        return Task.CompletedTask;
    }
}

/// <summary>Keeps the log entries that the real service writes, so the probe can read the refusal reason.</summary>
internal sealed class LogCapture
{
    private readonly List<(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State, Exception? Exception)> _entries = [];

    public void Clear() => _entries.Clear();

    public string? Property(string name) => _entries.SelectMany(entry => entry.State)
        .Where(pair => pair.Key == name).Select(pair => pair.Value?.ToString()).FirstOrDefault();

    public Exception? Failure => _entries.Select(entry => entry.Exception).FirstOrDefault(exception => exception is not null);

    public ILogger<T> For<T>() => new Logger<T>(this);

    private sealed class Logger<T>(LogCapture capture) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            capture._entries.Add((logLevel, formatter(state, exception), properties, exception));
        }
    }
}

/// <summary>Records the status and the body of the final provider response of a call.</summary>
internal sealed class ResponseCapture : PipelinePolicy
{
    public int Status { get; private set; }
    public string Body { get; private set; } = string.Empty;

    public void Reset()
    {
        Status = 0;
        Body = string.Empty;
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ProcessNext(message, pipeline, currentIndex);
        Capture(message);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        await ProcessNextAsync(message, pipeline, currentIndex);
        Capture(message);
    }

    private void Capture(PipelineMessage message)
    {
        if (message.Response is not { } response)
            return;
        Status = response.Status;
        response.BufferContent();
        Body = response.Content.ToString();
    }

    /// <summary>The assistant text of a Responses API body, or null when the body holds no assistant text.</summary>
    public string? AssistantText()
    {
        try
        {
            using var document = JsonDocument.Parse(Body);
            if (!document.RootElement.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
                return null;
            var parts = output.EnumerateArray()
                .Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "message")
                .SelectMany(item => item.GetProperty("content").EnumerateArray())
                .Where(part => part.TryGetProperty("type", out var type) && type.GetString() == "output_text")
                .Select(part => part.GetProperty("text").GetString());
            return string.Concat(parts);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
