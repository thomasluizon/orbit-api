using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Application.Common;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.AI;

public sealed partial class AiUsageRecorder(
    IServiceScopeFactory scopeFactory,
    IOptions<AiSettings> options,
    IProductAnalytics analytics,
    ILogger<AiUsageRecorder> logger) : IAiUsageRecorder
{
    private const decimal TokensPerMillion = 1_000_000m;

    private readonly IReadOnlyDictionary<string, AiModelPrice> _pricing = options.Value.Pricing;

    public async Task RecordAsync(
        string purpose,
        string model,
        long cachedTokens,
        long promptTokens,
        long completionTokens,
        long totalTokens,
        CancellationToken cancellationToken = default,
        Guid? userId = null,
        AiCallTelemetry? telemetry = null,
        long cacheWriteTokens = 0)
    {
        var costUsd = ComputeCostUsd(_pricing.GetValueOrDefault(model), cachedTokens, promptTokens, completionTokens, cacheWriteTokens);
#pragma warning disable ORBIT0004 // AiUsageDaily.Date partitions model cost counters by UTC accounting day; it is not the user quota or schedule date. https://github.com/thomasluizon/orbit-tickets/issues/230
        var date = DateOnly.FromDateTime(DateTime.UtcNow);
#pragma warning restore ORBIT0004

        try
        {
            if (telemetry is { HasUsage: false })
                return;
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "AiUsageDaily"
                    ("Id", "Date", "Model", "Purpose", "UserId", "Calls", "CachedTokens", "PromptTokens", "CompletionTokens", "TotalTokens", "CostUsd")
                VALUES ({Guid.NewGuid()}, {date}, {model}, {purpose}, {userId}, 1, {cachedTokens}, {promptTokens}, {completionTokens}, {totalTokens}, {costUsd})
                ON CONFLICT ("Date", "Model", "Purpose", "UserId") DO UPDATE SET
                    "Calls" = "AiUsageDaily"."Calls" + 1,
                    "CachedTokens" = "AiUsageDaily"."CachedTokens" + EXCLUDED."CachedTokens",
                    "PromptTokens" = "AiUsageDaily"."PromptTokens" + EXCLUDED."PromptTokens",
                    "CompletionTokens" = "AiUsageDaily"."CompletionTokens" + EXCLUDED."CompletionTokens",
                    "TotalTokens" = "AiUsageDaily"."TotalTokens" + EXCLUDED."TotalTokens",
                    "CostUsd" = "AiUsageDaily"."CostUsd" + EXCLUDED."CostUsd"
                """, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogUsageRecordFailed(logger, model, purpose, ex);
        }
        finally
        {
            var price = _pricing.GetValueOrDefault(model);
            var inputCost = ComputeCostUsd(price, cachedTokens, promptTokens, 0, cacheWriteTokens);
            var outputCost = ComputeCostUsd(price, 0, 0, completionTokens);
            var properties = new Dictionary<string, object>
            {
                ["$ai_trace_id"] = telemetry?.TraceId ?? Guid.NewGuid().ToString("N"),
                ["$ai_span_id"] = Guid.NewGuid().ToString("N"),
                ["$ai_model"] = model,
                ["$ai_provider"] = "openai",
                ["$ai_input_tokens"] = promptTokens,
                ["$ai_output_tokens"] = completionTokens,
                ["$ai_cache_read_input_tokens"] = cachedTokens,
                ["$ai_latency"] = telemetry?.LatencySeconds ?? 0d,
                ["$ai_is_error"] = telemetry?.IsError ?? false,
                ["$ai_input_cost_usd"] = inputCost,
                ["$ai_output_cost_usd"] = outputCost,
                ["$ai_total_cost_usd"] = costUsd,
                ["purpose"] = purpose,
                ["tool_names"] = telemetry?.ToolNames.ToArray() ?? []
            };
            if (userId is { } id)
                AnalyticsCapture.SafeCaptureUserEvent(analytics, logger, id, telemetry?.Plan, "$ai_generation", properties);
            else
                AnalyticsCapture.SafeCaptureAggregateEvent(analytics, logger, "$ai_generation", properties);
        }
    }

    /// <summary>
    /// Splits prompt tokens into uncached and cached input at their distinct prices and adds output cost.
    /// Returns 0 when the model has no configured price (the daily summary flags such models as unpriced).
    /// </summary>
    internal static decimal ComputeCostUsd(
        AiModelPrice? price, long cachedTokens, long promptTokens, long completionTokens, long cacheWriteTokens = 0)
    {
        if (price is null)
            return 0m;

        var uncachedInputTokens = promptTokens - cachedTokens - cacheWriteTokens;

        return uncachedInputTokens / TokensPerMillion * price.InputPerMillionUsd
            + cachedTokens / TokensPerMillion * price.CachedInputPerMillionUsd
            + cacheWriteTokens / TokensPerMillion * (price.CacheWritePerMillionUsd ?? price.InputPerMillionUsd)
            + completionTokens / TokensPerMillion * price.OutputPerMillionUsd;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to record AI usage for {Model} ({Purpose})")]
    private static partial void LogUsageRecordFailed(ILogger logger, string model, string purpose, Exception ex);
}
