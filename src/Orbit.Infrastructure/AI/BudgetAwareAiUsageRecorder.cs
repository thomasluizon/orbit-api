using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.AI;

public sealed class BudgetAwareAiUsageRecorder(AiUsageRecorder inner, IHttpContextAccessor http) : IAiUsageRecorder
{
    public Task RecordAsync(string purpose, string model, long cachedTokens, long promptTokens, long completionTokens,
        long totalTokens, CancellationToken cancellationToken = default, Guid? userId = null)
    {
        var context = AiCallContext.Current;
        context?.Report(totalTokens);
        var effectiveUser = userId ?? context?.UserId;
        if (effectiveUser is null && Guid.TryParse(http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var authenticatedUser))
            effectiveUser = authenticatedUser;
        return inner.RecordAsync(purpose, model, cachedTokens, promptTokens, completionTokens, totalTokens, cancellationToken, effectiveUser);
    }
}
