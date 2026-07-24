using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.AI;

/// <summary>
/// Singleton recorder that converts one completion's tokens into a dollar cost from the configured
/// per-model price map and atomically UPSERTs it into the daily (date, model, purpose) aggregate via a
/// child DI scope, so the singleton AI client never holds a scoped DbContext. Best-effort: any write
/// failure is logged once at Warning and swallowed so the user's AI response is never affected.
/// </summary>
public sealed partial class AiUsageRecorder(
    IServiceScopeFactory scopeFactory,
    IOptions<AiSettings> options,
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
        CancellationToken cancellationToken = default)
    {
        var costUsd = ComputeCostUsd(_pricing.GetValueOrDefault(model), cachedTokens, promptTokens, completionTokens);
#pragma warning disable ORBIT0004 // WHY: pre-existing deliberate UTC-date window or UTC-keyed dedupe/aggregation bucket (not a user's calendar date), per-site justification ledger: https://github.com/thomasluizon/orbit-api/issues/431
        var date = DateOnly.FromDateTime(DateTime.UtcNow);
#pragma warning restore ORBIT0004

        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "AiUsageDaily"
                    ("Id", "Date", "Model", "Purpose", "Calls", "CachedTokens", "PromptTokens", "CompletionTokens", "TotalTokens", "CostUsd")
                VALUES ({Guid.NewGuid()}, {date}, {model}, {purpose}, 1, {cachedTokens}, {promptTokens}, {completionTokens}, {totalTokens}, {costUsd})
                ON CONFLICT ("Date", "Model", "Purpose") DO UPDATE SET
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
    }

    /// <summary>
    /// Splits prompt tokens into uncached and cached input at their distinct prices and adds output cost.
    /// Returns 0 when the model has no configured price (the daily summary flags such models as unpriced).
    /// </summary>
    internal static decimal ComputeCostUsd(
        AiModelPrice? price, long cachedTokens, long promptTokens, long completionTokens)
    {
        if (price is null)
            return 0m;

        var uncachedInputTokens = promptTokens - cachedTokens;

        return uncachedInputTokens / TokensPerMillion * price.InputPerMillionUsd
            + cachedTokens / TokensPerMillion * price.CachedInputPerMillionUsd
            + completionTokens / TokensPerMillion * price.OutputPerMillionUsd;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to record AI usage for {Model} ({Purpose})")]
    private static partial void LogUsageRecordFailed(ILogger logger, string model, string purpose, Exception ex);
}
