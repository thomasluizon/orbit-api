using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Orbit.Domain.Interfaces;
using Microsoft.Extensions.Options;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.AI;

public sealed class BudgetAwareAiUsageRecorder(AiUsageRecorder inner, IHttpContextAccessor http, IOptions<AiSettings> settings) : IAiUsageRecorder
{
    public Task RecordAsync(string purpose, string model, long cachedTokens, long promptTokens, long completionTokens,
        long totalTokens, CancellationToken cancellationToken = default, Guid? userId = null, long cacheWriteTokens = 0)
    {
        var context = AiCallContext.Current;
        if (context is not null)
        {
            var price = AiRequestCostEstimator.GetPrice(settings.Value, model);
            var costUsd = AiUsageRecorder.ComputeCostUsd(price, cachedTokens, promptTokens, completionTokens, cacheWriteTokens);
            context.ReportCost(checked((long)decimal.Ceiling(costUsd * 1_000_000m)));
        }
        var effectiveUser = userId ?? context?.UserId;
        if (effectiveUser is null && Guid.TryParse(http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var authenticatedUser))
            effectiveUser = authenticatedUser;
        return inner.RecordAsync(purpose, model, cachedTokens, promptTokens, completionTokens, totalTokens, cancellationToken, effectiveUser, cacheWriteTokens);
    }
}
