using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Orbit.Api.Extensions;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Api.RateLimiting;

[AttributeUsage(AttributeTargets.Method)]
public sealed class SendCodeQuotaAttribute : Attribute, IFilterFactory, IOrderedFilter
{
    public int Order => 2;
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) => new SendCodeQuotaFilter(
        serviceProvider.GetRequiredService<IDistributedQuotaService>(),
        serviceProvider.GetRequiredService<IAppConfigService>(),
        serviceProvider.GetRequiredService<BffClientIpResolver>(),
        serviceProvider.GetRequiredService<TimeProvider>(),
        serviceProvider.GetRequiredService<ILogger<SendCodeQuotaFilter>>());
}

public sealed class SendCodeQuotaFilter(
    IDistributedQuotaService quotas,
    IAppConfigService config,
    BffClientIpResolver clientIpResolver,
    TimeProvider clock,
    ILogger<SendCodeQuotaFilter> logger) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var ct = context.HttpContext.RequestAborted;
        var ipLimit = await config.GetAsync(AppConfigKeys.SendCodeEmailsPerIpPerDay, 100, ct);
        var globalLimit = await config.GetAsync(AppConfigKeys.SendCodeEmailsPerDay, 10_000, ct);
        var ip = clientIpResolver.Resolve(context.HttpContext).Address ?? "unknown";
        if (!await quotas.TryAcquireAsync("send-code-ip-day", $"ip:{ip}", ipLimit, 1, TimeSpan.FromDays(1), ct))
        {
            Reject(context);
            return;
        }
        if (!await quotas.TryAcquireAsync("send-code-global-day", "global", globalLimit, 1, TimeSpan.FromDays(1), ct))
        {
            logger.LogWarning("Global daily code email quota exhausted. Limit={Limit} RequestId={RequestId}",
                globalLimit, context.HttpContext.GetRequestId());
            Reject(context);
            return;
        }
        await next();
    }

    private void Reject(ActionExecutingContext context)
    {
        var nowAtUtc = clock.GetUtcNow();
        var retryAfter = Math.Max(1, (int)Math.Ceiling((nowAtUtc.UtcDateTime.Date.AddDays(1) - nowAtUtc.UtcDateTime).TotalSeconds));
        context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        context.HttpContext.Response.Headers[Orbit.Api.Extensions.HttpContextExtensions.RequestIdHeaderName] = context.HttpContext.GetRequestId();
        context.Result = new ObjectResult(ErrorMessages.TooManyRequests.ToErrorBody()) { StatusCode = StatusCodes.Status429TooManyRequests };
    }
}
