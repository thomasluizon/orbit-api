using System.Reflection;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Api.Controllers;
using Orbit.Api.RateLimiting;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Common;
using Orbit.Application.Waitlist.Commands;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.RateLimiting;

[Collection("ProcessEnvironment")]
public class RequireBotProtectionFilterTests
{
    private readonly ITurnstileVerificationService _verifier = Substitute.For<ITurnstileVerificationService>();
    private readonly ILogger<RequireBotProtectionFilter> _logger = Substitute.For<ILogger<RequireBotProtectionFilter>>();

    [Theory]
    [InlineData(nameof(AuthController.SendCode), "send-code")]
    [InlineData(nameof(AuthController.SendCodeOperation), "operations/send-code")]
    [InlineData(nameof(AuthController.VerifyCode), "verify-code")]
    [InlineData(nameof(AuthController.VerifyCodeOperation), "operations/verify-code")]
    [InlineData(nameof(WaitlistController.Join), "")]
    public async Task ProtectedPost_MissingToken_StopsBeforeCommand(string action, string route)
    {
        var controller = action == nameof(WaitlistController.Join) ? typeof(WaitlistController) : typeof(AuthController);
        var method = controller.GetMethod(action)!;
        method.GetCustomAttribute<HttpPostAttribute>()!.Template.Should().Be(route == "" ? null : route);
        method.GetCustomAttribute<RequireBotProtectionAttribute>().Should().NotBeNull();
        method.GetCustomAttribute<DistributedRateLimitAttribute>().Should().NotBeNull();

        var context = CreateContext(RequestFor(action, null));
        var commandRan = false;
        await EnabledFilter().OnActionExecutionAsync(context, () =>
        {
            commandRan = true;
            return Task.FromResult(Executed(context));
        });

        commandRan.Should().BeFalse();
        var result = context.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        result.Value.Should().BeEquivalentTo(new { error = "Invalid verification token", requestId = "req_bot_protection" });
        await _verifier.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default);
    }

    [Theory]
    [InlineData(nameof(AuthController.SendCode))]
    [InlineData(nameof(AuthController.SendCodeOperation))]
    [InlineData(nameof(AuthController.VerifyCode))]
    [InlineData(nameof(AuthController.VerifyCodeOperation))]
    [InlineData(nameof(WaitlistController.Join))]
    public async Task Disabled_LeavesEveryRouteUnchanged(string action)
    {
        var context = CreateContext(RequestFor(action, null));
        var commandRan = false;
        await Filter(enabled: false).OnActionExecutionAsync(context, () =>
        {
            commandRan = true;
            return Task.FromResult(Executed(context));
        });

        commandRan.Should().BeTrue();
        context.Result.Should().BeNull();
        await _verifier.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default);
    }

    [Fact]
    public async Task SendCode_MissingToken_DoesNotCacheOrEnqueue()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var jobs = Substitute.For<IBackgroundJobClient>();
        var handler = new SendCodeCommandHandler(cache, jobs);
        var context = CreateContext(new AuthController.SendCodeRequest("attacker-chosen@example.com"));

        await EnabledFilter().OnActionExecutionAsync(context, async () =>
        {
            await handler.Handle(new SendCodeCommand("attacker-chosen@example.com"), CancellationToken.None);
            return Executed(context);
        });

        context.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        cache.TryGetValue("verify:attacker-chosen@example.com", out _).Should().BeFalse();
        jobs.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
    }

    [Fact]
    public async Task SendCode_ValidToken_CachesAndEnqueuesOnce()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var jobs = Substitute.For<IBackgroundJobClient>();
        var handler = new SendCodeCommandHandler(cache, jobs);
        _verifier.VerifyAsync("valid", Arg.Any<CancellationToken>())
            .Returns(new TurnstileVerificationResult(true, []));
        var context = CreateContext(new AuthController.SendCodeRequest("person@example.com", TurnstileToken: "valid"));

        await EnabledFilter().OnActionExecutionAsync(context, async () =>
        {
            await handler.Handle(new SendCodeCommand("person@example.com"), CancellationToken.None);
            return Executed(context);
        });

        context.Result.Should().BeNull();
        cache.TryGetValue("verify:person@example.com", out _).Should().BeTrue();
        jobs.Received(1).Create(Arg.Any<Job>(), Arg.Any<IState>());
    }

    [Fact]
    public async Task Waitlist_MissingToken_DoesNotSendEmailOrCache()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var email = Substitute.For<IEmailService>();
        var tokens = Substitute.For<IWaitlistConfirmationTokenService>();
        var handler = new JoinWaitlistCommandHandler(
            cache,
            tokens,
            email,
            Options.Create(new WaitlistSettings()));
        var context = CreateContext(new WaitlistController.JoinWaitlistRequest("person@example.com"));

        await EnabledFilter().OnActionExecutionAsync(context, async () =>
        {
            await handler.Handle(new JoinWaitlistCommand("person@example.com"), CancellationToken.None);
            return Executed(context);
        });

        context.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        cache.TryGetValue("waitlist:person@example.com", out _).Should().BeFalse();
        await email.DidNotReceiveWithAnyArgs().SendWaitlistConfirmationAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task RejectedToken_Returns400BeforeCommand()
    {
        _verifier.VerifyAsync("rejected", Arg.Any<CancellationToken>())
            .Returns(new TurnstileVerificationResult(false, ["invalid-input-response"]));
        var context = CreateContext(new AuthController.SendCodeRequest("person@example.com", TurnstileToken: "rejected"));
        var commandRan = false;

        await EnabledFilter().OnActionExecutionAsync(context, () =>
        {
            commandRan = true;
            return Task.FromResult(Executed(context));
        });

        context.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        commandRan.Should().BeFalse();
    }

    [Fact]
    public async Task VerifierTransportFailure_Returns503BeforeCommand()
    {
        _verifier.VerifyAsync("token", Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("network down"));
        var context = CreateContext(new AuthController.SendCodeRequest("person@example.com", TurnstileToken: "token"));
        var commandRan = false;

        await EnabledFilter().OnActionExecutionAsync(context, () =>
        {
            commandRan = true;
            return Task.FromResult(Executed(context));
        });

        context.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(503);
        commandRan.Should().BeFalse();
    }

    [Fact]
    public async Task RateLimitRejectsBeforeBotVerification_WithRetryHeader()
    {
        new RequireBotProtectionAttribute().Order.Should().BeGreaterThan(0);
        var rateLimits = Substitute.For<IDistributedRateLimitService>();
        rateLimits.TryAcquireAsync("auth", "auth:email:person@example.com", Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(false, 11, 10, DateTime.UtcNow.AddMinutes(1)));
        var rateFilter = new DistributedRateLimitFilter(
            "auth",
            rateLimits,
            Substitute.For<IAuthSessionService>(),
            Substitute.For<ILogger<DistributedRateLimitFilter>>());
        var context = CreateContext(new AuthController.SendCodeRequest("person@example.com", TurnstileToken: "token"));
        var commandRan = false;

        await rateFilter.OnActionExecutionAsync(context, async () =>
        {
            await EnabledFilter().OnActionExecutionAsync(context, () =>
            {
                commandRan = true;
                return Task.FromResult(Executed(context));
            });
            return Executed(context);
        });

        context.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(429);
        context.HttpContext.Response.Headers.RetryAfter.Should().NotBeEmpty();
        commandRan.Should().BeFalse();
        await _verifier.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default);
    }

    [Fact]
    public async Task SmokeAccount_OnlyConfiguredProductionAddressBypasses()
    {
        var oldEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var oldEmail = Environment.GetEnvironmentVariable("SMOKE_TEST_EMAIL");
        var oldCode = Environment.GetEnvironmentVariable("SMOKE_TEST_CODE");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
            Environment.SetEnvironmentVariable("SMOKE_TEST_EMAIL", "smoke@useorbit.org");
            Environment.SetEnvironmentVariable("SMOKE_TEST_CODE", "428913");

            var smoke = CreateContext(new AuthController.SendCodeRequest("SMOKE@useorbit.org"));
            var commandRan = false;
            await EnabledFilter().OnActionExecutionAsync(smoke, () =>
            {
                commandRan = true;
                return Task.FromResult(Executed(smoke));
            });
            commandRan.Should().BeTrue();

            var other = CreateContext(new AuthController.SendCodeRequest("other@useorbit.org"));
            await EnabledFilter().OnActionExecutionAsync(other, () => Task.FromResult(Executed(other)));
            other.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", oldEnvironment);
            Environment.SetEnvironmentVariable("SMOKE_TEST_EMAIL", oldEmail);
            Environment.SetEnvironmentVariable("SMOKE_TEST_CODE", oldCode);
        }
    }

    private RequireBotProtectionFilter EnabledFilter() => Filter(enabled: true);

    private RequireBotProtectionFilter Filter(bool enabled)
        => new(Options.Create(new BotProtectionSettings { Enabled = enabled, SecretKey = "test-secret" }), _verifier, _logger);

    private static object RequestFor(string action, string? token) => action switch
    {
        nameof(AuthController.SendCode) => new AuthController.SendCodeRequest("person@example.com", TurnstileToken: token),
        nameof(AuthController.SendCodeOperation) => new AuthController.SendCodeOperationRequest("person@example.com", TurnstileToken: token),
        nameof(AuthController.VerifyCode) => new AuthController.VerifyCodeRequest("person@example.com", "123456", TurnstileToken: token),
        nameof(AuthController.VerifyCodeOperation) => new AuthController.VerifyCodeOperationRequest("person@example.com", "123456", TurnstileToken: token),
        nameof(WaitlistController.Join) => new WaitlistController.JoinWaitlistRequest("person@example.com", TurnstileToken: token),
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private static ActionExecutingContext CreateContext(object request)
    {
        var http = new DefaultHttpContext { TraceIdentifier = "req_bot_protection" };
        http.Request.Method = "POST";
        return new ActionExecutingContext(
            new ActionContext(http, new RouteData(), new ControllerActionDescriptor()),
            [],
            new Dictionary<string, object?> { ["request"] = request },
            new object());
    }

    private static ActionExecutedContext Executed(ActionExecutingContext context)
        => new(context, context.Filters, context.Controller);
}
