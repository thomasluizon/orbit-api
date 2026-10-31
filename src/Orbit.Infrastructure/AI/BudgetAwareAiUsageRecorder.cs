using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Orbit.Application.Common;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.AI;

public sealed class BudgetAwareAiUsageRecorder(AiUsageRecorder inner, IHttpContextAccessor http, IOptions<AiSettings> settings,
    IConfiguration? configuration = null) : IAiUsageRecorder
{
    public Task RecordAsync(string purpose, string model, long cachedTokens, long promptTokens, long completionTokens,
        long totalTokens, CancellationToken cancellationToken = default, Guid? userId = null, AiCallTelemetry? telemetry = null, long cacheWriteTokens = 0, long reasoningTokens = 0)
    {
        var context = AiCallContext.Current;
        if (context is not null && telemetry is not { HasUsage: false })
        {
            var price = AiRequestCostEstimator.GetPrice(settings.Value, model);
            var costUsd = AiUsageRecorder.ComputeCostUsd(price, cachedTokens, promptTokens, completionTokens, cacheWriteTokens);
            context.ReportCost(checked((long)decimal.Ceiling(costUsd * 1_000_000m)));
        }
        var effectiveUser = userId ?? context?.UserId;
        if (effectiveUser is null && Guid.TryParse(http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var authenticatedUser))
            effectiveUser = authenticatedUser;
        if (effectiveUser is { } evalUser && EvalAccountAccess.Contains(configuration?[EvalAccountAccess.AccountIdsKey], evalUser))
            purpose = "eval_" + purpose;
        return inner.RecordAsync(purpose, model, cachedTokens, promptTokens, completionTokens, totalTokens, cancellationToken, effectiveUser, telemetry, cacheWriteTokens, reasoningTokens);
    }
}
