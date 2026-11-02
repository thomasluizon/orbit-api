using System.Net;
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.RateLimiting;

public sealed class SendCodeQuotaTests : IDisposable
{
    private readonly OrbitDbContext db = new(new DbContextOptionsBuilder<OrbitDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly IAppConfigService config = Substitute.For<IAppConfigService>();
    private readonly ILogger<SendCodeQuotaFilter> logger = Substitute.For<ILogger<SendCodeQuotaFilter>>();
    private readonly Clock clock = new();

    [Theory]
    [InlineData(nameof(AuthController.SendCode))]
    [InlineData(nameof(AuthController.SendCodeOperation))]
    [InlineData("OAuth")]
    public async Task EverySendRoute_RejectsPerIpAndGlobalDailyExhaustion(string route)
    {
        config.GetAsync(AppConfigKeys.SendCodeEmailsPerIpPerDay, 100, Arg.Any<CancellationToken>()).Returns(2);
        config.GetAsync(AppConfigKeys.SendCodeEmailsPerDay, 10000, Arg.Any<CancellationToken>()).Returns(3);
        var action = route == "OAuth" ? typeof(OAuthController).GetMethod(nameof(OAuthController.SendCode))! : typeof(AuthController).GetMethod(route)!;
        var attribute = action.GetCustomAttribute<SendCodeQuotaAttribute>();
        attribute.Should().NotBeNull();
        attribute!.Order.Should().BeGreaterThan(new RequireBotProtectionAttribute().Order);
        using var provider = new ServiceCollection().AddSingleton<IDistributedQuotaService>(new DistributedRateLimitService(db, clock))
            .AddSingleton(config).AddSingleton(new BffClientIpResolver(Options.Create(new BffForwardingSettings { BffForwardingSecret = "forwarding-secret" })))
            .AddSingleton<TimeProvider>(clock).AddSingleton(logger).BuildServiceProvider();
        var filter = (IAsyncActionFilter)attribute.CreateInstance(provider);
        async Task Request(string ip, bool allowed)
        {
            var http = new DefaultHttpContext();
            http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
            http.Request.Headers["X-Orbit-Bff-Secret"] = "forwarding-secret";
            http.Request.Headers["X-Orbit-Client-Ip"] = ip;
            var context = new ActionExecutingContext(new ActionContext(http, new RouteData(), new ControllerActionDescriptor()), [], new Dictionary<string, object?>(), new object());
            var ran = false;
            await filter.OnActionExecutionAsync(context, () =>
            {
                ran = true;
                return Task.FromResult(new ActionExecutedContext(context, [], context.Controller));
            });
            ran.Should().Be(allowed);
            if (!allowed)
            {
                context.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(429);
                int.Parse(http.Response.Headers.RetryAfter.ToString()).Should().BeGreaterThan(0);
            }
        }
        await Request("198.51.100.1", true);
        await Request("198.51.100.1", true);
        await Request("198.51.100.1", false);
        await Request("198.51.100.2", true);
        await Request("198.51.100.3", false);
        logger.ReceivedCalls().Should().Contain(call => call.GetMethodInfo().Name == "Log" && (LogLevel)call.GetArguments()[0]! == LogLevel.Warning);
        db.DistributedRateLimitBuckets.Single(b => b.PolicyName == "send-code-global-day").Count.Should().Be(3);
        clock.Now = clock.Now.AddDays(1);
        await Request("198.51.100.1", true);
    }

    public void Dispose() => db.Dispose();
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
