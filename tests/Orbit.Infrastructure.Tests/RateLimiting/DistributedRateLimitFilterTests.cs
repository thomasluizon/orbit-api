using FluentAssertions;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Security.Claims;
using System.Reflection;
using Orbit.Api.Controllers;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Auth.Validators;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.RateLimiting;

public class DistributedRateLimitFilterTests
{
    private readonly IDistributedRateLimitService _service = Substitute.For<IDistributedRateLimitService>();
    private readonly IAuthSessionService _authSessionService = Substitute.For<IAuthSessionService>();
    private readonly ILogger<DistributedRateLimitFilter> _logger = Substitute.For<ILogger<DistributedRateLimitFilter>>();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnswerPolicy_OnlyAppliesWhenBlockAnswerIsPresent(bool hasAnswer)
    {
        var filter = new DistributedRateLimitFilter("ai-resolve", _service, _authSessionService, new BffClientIpResolver(Options.Create(new BffForwardingSettings())), _logger, blockAnswersOnly: true);
        _service.TryAcquireAsync("ai-resolve", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(false, 10, 10, DateTime.UtcNow.AddMinutes(1)));
        var (context, _) = CreateExecutingContext();
        context.ActionArguments["blockAnswer"] = hasAnswer ? "{}" : null;
        var called = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            called = true;
            return Task.FromResult(CreateExecutedContext(context));
        });
        called.Should().Be(!hasAnswer);
        await _service.Received(hasAnswer ? 1 : 0).TryAcquireAsync("ai-resolve", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(nameof(AuthController.GoogleCodeAuth))]
    [InlineData(nameof(AuthController.GoogleCodeAuthOperation))]
    public async Task GoogleCodeRoute_UsesAuthRateLimitPolicy(string action)
    {
        _service.TryAcquireAsync("auth", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(true, 1, 10, DateTime.UtcNow.AddMinutes(1)));
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IDistributedRateLimitService)).Returns(_service);
        provider.GetService(typeof(IAuthSessionService)).Returns(_authSessionService);
        provider.GetService(typeof(BffClientIpResolver)).Returns(new BffClientIpResolver(Options.Create(new BffForwardingSettings())));
        provider.GetService(typeof(ILogger<DistributedRateLimitFilter>)).Returns(_logger);
        var attribute = typeof(AuthController).GetMethod(action)!
            .GetCustomAttribute<DistributedRateLimitAttribute>()!;
        var filter = (DistributedRateLimitFilter)attribute.CreateInstance(provider);
        var (context, _) = CreateExecutingContext();

        await filter.OnActionExecutionAsync(context, () => Task.FromResult(CreateExecutedContext(context)));

        await _service.Received(1).TryAcquireAsync("auth", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SupportPolicy_FailsOpen_WhenRateLimitStoreUnavailable()
    {
        _service.TryAcquireAsync("support", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("rate-limit store down"));

        var filter = new DistributedRateLimitFilter("support", _service, _authSessionService, new BffClientIpResolver(Options.Create(new BffForwardingSettings())), _logger);
        var (context, _) = CreateExecutingContext();
        var nextCalled = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(CreateExecutedContext(context));
        });

        nextCalled.Should().BeTrue();
        context.Result.Should().BeNull();
    }

    [Fact]
    public async Task NonSupportPolicy_FailsClosed_WhenRateLimitStoreUnavailable()
    {
        _service.TryAcquireAsync("chat", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("rate-limit store down"));

        var filter = new DistributedRateLimitFilter("chat", _service, _authSessionService, new BffClientIpResolver(Options.Create(new BffForwardingSettings())), _logger);
        var (context, _) = CreateExecutingContext();
        var nextCalled = false;

        var act = () => filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(CreateExecutedContext(context));
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        nextCalled.Should().BeFalse();
    }

    [Theory]
    [InlineData("support")]
    [InlineData("chat")]
    public async Task CanceledAcquisition_NeverInvokesAction(string policy)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var failure = new OperationCanceledException(cancellation.Token);
        _service.TryAcquireAsync(policy, Arg.Any<string>(), cancellation.Token).ThrowsAsync(failure);
        var filter = new DistributedRateLimitFilter(policy, _service, _authSessionService,
            new BffClientIpResolver(Options.Create(new BffForwardingSettings())), _logger);
        var (context, _) = CreateExecutingContext();
        context.HttpContext.RequestAborted = cancellation.Token;
        var actionCalls = 0;
        var execute = () => filter.OnActionExecutionAsync(context, () =>
        {
            actionCalls++;
            return Task.FromResult(CreateExecutedContext(context));
        });

        await execute.Should().ThrowAsync<OperationCanceledException>();

        actionCalls.Should().Be(0);
        await _service.Received(1).TryAcquireAsync(policy, Arg.Any<string>(), cancellation.Token);
    }

    [Fact]
    public async Task SupportPolicy_StillThrottles_WhenStoreReportsLimitExceeded()
    {
        _service.TryAcquireAsync("support", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(false, 3, 3, DateTime.UtcNow.AddHours(1)));

        var filter = new DistributedRateLimitFilter("support", _service, _authSessionService, new BffClientIpResolver(Options.Create(new BffForwardingSettings())), _logger);
        var (context, _) = CreateExecutingContext();
        var nextCalled = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(CreateExecutedContext(context));
        });

        nextCalled.Should().BeFalse();
        context.Result.Should().NotBeNull();
    }

    [Fact]
    public async Task RefreshPolicy_PartitionsUnauthenticatedRequestByRefreshToken_WhenTokenMapsToRealSession()
    {
        var refreshToken = new string('A', RefreshTokenRules.TokenLength);
        _authSessionService.HasSessionForTokenAsync(refreshToken, Arg.Any<CancellationToken>()).Returns(true);
        string? capturedPartitionKey = null;
        _service.TryAcquireAsync("refresh", Arg.Do<string>(key => capturedPartitionKey = key), Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(true, 1, 10, DateTime.UtcNow.AddMinutes(1)));

        var filter = new DistributedRateLimitFilter("refresh", _service, _authSessionService, new BffClientIpResolver(Options.Create(new BffForwardingSettings())), _logger);
        var (context, _) = CreateExecutingContext(new AuthController.RefreshSessionRequest(refreshToken));

        await filter.OnActionExecutionAsync(context, () => Task.FromResult(CreateExecutedContext(context)));

        capturedPartitionKey.Should().StartWith("refresh:token:");
        capturedPartitionKey.Should().NotContain(refreshToken);
    }

    [Fact]
    public async Task RefreshPolicy_FallsBackToIpPartition_WhenWellFormedTokenHasNoRealSession()
    {
        var forgedToken = new string('A', RefreshTokenRules.TokenLength);
        _authSessionService.HasSessionForTokenAsync(forgedToken, Arg.Any<CancellationToken>()).Returns(false);
        string? capturedPartitionKey = null;
        _service.TryAcquireAsync("refresh", Arg.Do<string>(key => capturedPartitionKey = key), Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(true, 1, 10, DateTime.UtcNow.AddMinutes(1)));

        var filter = new DistributedRateLimitFilter("refresh", _service, _authSessionService, new BffClientIpResolver(Options.Create(new BffForwardingSettings())), _logger);
        var (context, httpContext) = CreateExecutingContext(new AuthController.RefreshSessionRequest(forgedToken));
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");

        await filter.OnActionExecutionAsync(context, () => Task.FromResult(CreateExecutedContext(context)));

        capturedPartitionKey.Should().StartWith("ip:");
        capturedPartitionKey.Should().NotStartWith("refresh:token:");
    }

    [Fact]
    public async Task ConcurrentChatLimit_SecondRequest_ReturnsMatchingRateLimitResponse()
    {
        var userId = Guid.NewGuid();
        _service.TryAcquireLeaseAsync(
                ConcurrentChatLimitFilter.PolicyName,
                $"user:{userId}",
                1,
                TimeSpan.FromMinutes(5),
                Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(false, 1, 1, DateTime.UtcNow.AddMinutes(4)));
        var filter = new ConcurrentChatLimitFilter(
            _service,
            Substitute.For<ILogger<ConcurrentChatLimitFilter>>());
        var (context, httpContext) = CreateAuthenticatedExecutingContext(userId);
        httpContext.TraceIdentifier = "req_concurrent_chat";

        await filter.OnActionExecutionAsync(
            context,
            () => Task.FromResult(CreateExecutedContext(context)));

        var result = context.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        httpContext.Response.Headers.RetryAfter.Should().NotBeEmpty();
        httpContext.Response.Headers["X-Orbit-Request-Id"].ToString().Should().Be("req_concurrent_chat");
        await _service.DidNotReceiveWithAnyArgs().ReleaseLeaseAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task ConcurrentChatLimit_KeyedStream_DefersLeaseUntilTurnReservation()
    {
        var filter = new ConcurrentChatLimitFilter(_service, Substitute.For<ILogger<ConcurrentChatLimitFilter>>());
        var (context, http) = CreateAuthenticatedExecutingContext(Guid.NewGuid());
        http.Request.Headers["Idempotency-Key"] = "turn-key";
        var descriptor = (ControllerActionDescriptor)context.ActionDescriptor;
        descriptor.ControllerTypeInfo = typeof(ChatController).GetTypeInfo();
        descriptor.MethodInfo = typeof(ChatController).GetMethod(nameof(ChatController.ProcessChatStream))!;
        var called = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            called = true;
            return Task.FromResult(CreateExecutedContext(context));
        });

        called.Should().BeTrue();
        await _service.DidNotReceiveWithAnyArgs().TryAcquireLeaseAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task ConcurrentChatLimit_Success_ReleasesLease()
    {
        var userId = Guid.NewGuid();
        SetupAllowedLease(userId);
        var filter = new ConcurrentChatLimitFilter(
            _service,
            Substitute.For<ILogger<ConcurrentChatLimitFilter>>());
        var (context, _) = CreateAuthenticatedExecutingContext(userId);

        await filter.OnActionExecutionAsync(
            context,
            () => Task.FromResult(CreateExecutedContext(context)));

        await _service.Received(1).ReleaseLeaseAsync(
            ConcurrentChatLimitFilter.PolicyName,
            $"user:{userId}",
            Arg.Any<DateTime>(),
            CancellationToken.None);
    }

    [Fact]
    public async Task ConcurrentChatLimit_ActionException_ReleasesLease()
    {
        var userId = Guid.NewGuid();
        SetupAllowedLease(userId);
        var filter = new ConcurrentChatLimitFilter(
            _service,
            Substitute.For<ILogger<ConcurrentChatLimitFilter>>());
        var (context, _) = CreateAuthenticatedExecutingContext(userId);

        var act = () => filter.OnActionExecutionAsync(
            context,
            () => Task.FromException<ActionExecutedContext>(new InvalidOperationException("action failed")));

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _service.Received(1).ReleaseLeaseAsync(
            ConcurrentChatLimitFilter.PolicyName,
            $"user:{userId}",
            Arg.Any<DateTime>(),
            CancellationToken.None);
    }

    [Fact]
    public async Task ConcurrentChatLimit_ClientCancellation_ReleasesLease()
    {
        var userId = Guid.NewGuid();
        SetupAllowedLease(userId);
        var filter = new ConcurrentChatLimitFilter(
            _service,
            Substitute.For<ILogger<ConcurrentChatLimitFilter>>());
        var (context, _) = CreateAuthenticatedExecutingContext(userId);

        var act = () => filter.OnActionExecutionAsync(
            context,
            () => Task.FromException<ActionExecutedContext>(new OperationCanceledException("client disconnected")));

        await act.Should().ThrowAsync<OperationCanceledException>();
        await _service.Received(1).ReleaseLeaseAsync(
            ConcurrentChatLimitFilter.PolicyName,
            $"user:{userId}",
            Arg.Any<DateTime>(),
            CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthRoutes_RotatingEmails_ExhaustIpCeiling(bool oauth)
    {
        using var scope = new RateLimitFilterTestScope();
        var action = oauth
            ? typeof(OAuthController).GetMethod(nameof(OAuthController.VerifyCode))!
            : typeof(AuthController).GetMethod(nameof(AuthController.SendCode))!;
        var accepted = 0;
        for (var attempt = 1; attempt <= 40; attempt++)
        {
            object request = oauth
                ? new OAuthController.VerifyCodeRequest($"a{attempt}@x.com", "123456", "state", "challenge", "https://claude.ai/callback", "client")
                : new AuthController.SendCodeRequest($"a{attempt}@x.com");
            var context = scope.CreateContext(request, action);
            await scope.ExecuteAsync(context, () => accepted++);
            RateLimitFilterTestScope.AssertOutcome(context, attempt <= 30);
        }
        accepted.Should().Be(30);
    }

    [Theory]
    [InlineData("auth", 10, 60)]
    [InlineData("waitlist", 5, 600)]
    public async Task SameEmail_AcrossSources_ExhaustsOriginalEmailCeiling(string policy, int limit, int windowSeconds)
    {
        using var scope = new RateLimitFilterTestScope();
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            var context = scope.CreateContext(new AuthController.SendCodeRequest("a@x.com"));
            context.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse($"203.0.113.{attempt}");
            await scope.ExecuteAsync(context, policy: policy);
            RateLimitFilterTestScope.AssertOutcome(context, attempt <= limit);
        }
        var bucket = await scope.DbContext.DistributedRateLimitBuckets
            .SingleAsync(b => b.PolicyName == policy);
        (bucket.WindowEndsAtUtc - bucket.WindowStartUtc).TotalSeconds.Should().Be(windowSeconds);
        bucket.Count.Should().Be(limit);
    }

    [Fact]
    public async Task AuthPolicy_NineRequests_ConsumeEachDimensionOnce()
    {
        using var scope = new RateLimitFilterTestScope();
        for (var attempt = 0; attempt < 9; attempt++)
        {
            var context = scope.CreateContext(new AuthController.SendCodeRequest("a@x.com"));
            await scope.ExecuteAsync(context);
            RateLimitFilterTestScope.AssertOutcome(context, true);
        }
        var buckets = await scope.DbContext.DistributedRateLimitBuckets.ToListAsync();
        buckets.Should().HaveCount(2).And.OnlyContain(b => b.Count == 9);
    }

    [Fact]
    public async Task AuthPolicy_AuthenticatedCaller_ConsultsOnlyUserPartition()
    {
        var userId = Guid.NewGuid();
        _service.TryAcquireAsync("auth", $"user:{userId}", Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(true, 10, 1, DateTime.UtcNow.AddMinutes(1)));
        var (context, _) = CreateAuthenticatedExecutingContext(userId);
        context.ActionArguments["request"] = new AuthController.SendCodeRequest("a@x.com");
        var filter = new DistributedRateLimitFilter("auth", _service, _authSessionService, new BffClientIpResolver(Options.Create(new BffForwardingSettings())), _logger);

        await filter.OnActionExecutionAsync(context, () => Task.FromResult(CreateExecutedContext(context)));

        await _service.Received(1).TryAcquireAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _service.Received(1).TryAcquireAsync("auth", $"user:{userId}", Arg.Any<CancellationToken>());
        _authSessionService.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task MissingClientIp_RotatingEmails_SharesUnknownBucket()
    {
        using var scope = new RateLimitFilterTestScope();
        for (var attempt = 1; attempt <= 40; attempt++)
        {
            var context = scope.CreateContext(new AuthController.SendCodeRequest($"a{attempt}@x.com"));
            context.HttpContext.Connection.RemoteIpAddress = null;
            await scope.ExecuteAsync(context);
            RateLimitFilterTestScope.AssertOutcome(context, attempt <= 30);
        }
        var bucket = await scope.DbContext.DistributedRateLimitBuckets.SingleAsync(b => b.PolicyName == "auth-ip");
        bucket.PartitionKey.Should().Be("ip:unknown");
        bucket.Count.Should().Be(30);
    }

    [Fact]
    public async Task SharedNat_ThreeAccounts_KeepIndependentEmailAllowances()
    {
        using var scope = new RateLimitFilterTestScope();
        for (var account = 0; account < 3; account++)
        {
            for (var attempt = 0; attempt < 9; attempt++)
            {
                var context = scope.CreateContext(new AuthController.SendCodeRequest($"a{account}@x.com"));
                await scope.ExecuteAsync(context);
                RateLimitFilterTestScope.AssertOutcome(context, true);
            }
        }
        var buckets = await scope.DbContext.DistributedRateLimitBuckets.ToListAsync();
        buckets.Where(b => b.PolicyName == "auth").Should().HaveCount(3).And.OnlyContain(b => b.Count == 9);
        buckets.Single(b => b.PolicyName == "auth-ip").Count.Should().Be(27);
    }

    [Theory]
    [InlineData("auth", 30)]
    [InlineData("waitlist", 15)]
    public async Task VerifiedBff_DistinctClients_HaveIndependentIpBuckets(string policy, int limit)
    {
        using var scope = new RateLimitFilterTestScope("test-forwarding-secret");
        for (var client = 1; client <= 2; client++)
        {
            for (var attempt = 1; attempt <= limit; attempt++)
            {
                var context = scope.CreateContext(new AuthController.SendCodeRequest($"client{client}-{attempt}@x.com"));
                context.HttpContext.Request.Headers["X-Orbit-Bff-Secret"] = "test-forwarding-secret";
                context.HttpContext.Request.Headers["X-Orbit-Client-Ip"] = $"198.51.100.{client}";
                await scope.ExecuteAsync(context, policy: policy);
                RateLimitFilterTestScope.AssertOutcome(context, true);
            }
        }

        var buckets = await scope.DbContext.DistributedRateLimitBuckets
            .Where(b => b.PolicyName == $"{policy}-ip").ToListAsync();
        buckets.Select(b => b.PartitionKey).Should().BeEquivalentTo(["ip:198.51.100.1", "ip:198.51.100.2"]);
        buckets.Should().OnlyContain(b => b.Count == limit);
    }

    [Theory]
    [InlineData("auth", 30)]
    [InlineData("waitlist", 15)]
    public async Task VerifiedBff_RotatingEmails_ExhaustsOnlyEndClientBucket(string policy, int limit)
    {
        using var scope = new RateLimitFilterTestScope("test-forwarding-secret");
        for (var attempt = 1; attempt <= limit + 5; attempt++)
        {
            var context = scope.CreateContext(new AuthController.SendCodeRequest($"a{attempt}@x.com"));
            context.HttpContext.Request.Headers["X-Orbit-Bff-Secret"] = "test-forwarding-secret";
            context.HttpContext.Request.Headers["X-Orbit-Client-Ip"] = "198.51.100.1";
            await scope.ExecuteAsync(context, policy: policy);
            RateLimitFilterTestScope.AssertOutcome(context, attempt <= limit);
        }

        var otherClient = scope.CreateContext(new AuthController.SendCodeRequest("other@x.com"));
        otherClient.HttpContext.Request.Headers["X-Orbit-Bff-Secret"] = "test-forwarding-secret";
        otherClient.HttpContext.Request.Headers["X-Orbit-Client-Ip"] = "198.51.100.2";
        await scope.ExecuteAsync(otherClient, policy: policy);
        RateLimitFilterTestScope.AssertOutcome(otherClient, true);
    }

    [Theory]
    [InlineData("test-forwarding-secret", null)]
    [InlineData("test-forwarding-secret", "wrong-forwarding-secret")]
    [InlineData("test-forwarding-secret", "Test-forwarding-secret")]
    [InlineData(null, "test-forwarding-secret")]
    [InlineData("", "")]
    [InlineData(" ", " ")]
    public async Task UntrustedBffHeaders_CannotEscapeDirectSourceCeiling(string? configuredSecret, string? suppliedSecret)
    {
        using var scope = new RateLimitFilterTestScope(configuredSecret);
        for (var attempt = 1; attempt <= 40; attempt++)
        {
            var context = scope.CreateContext(new AuthController.SendCodeRequest($"a{attempt}@x.com"));
            if (suppliedSecret is not null)
                context.HttpContext.Request.Headers["X-Orbit-Bff-Secret"] = suppliedSecret;
            context.HttpContext.Request.Headers["X-Orbit-Client-Ip"] = $"198.51.100.{attempt}";
            context.HttpContext.Request.Headers["X-Forwarded-For"] = $"198.51.100.{attempt}";
            context.HttpContext.Request.Headers["Forwarded"] = $"for=198.51.100.{attempt}";
            await scope.ExecuteAsync(context);
            RateLimitFilterTestScope.AssertOutcome(context, attempt <= 30);
        }

        var bucket = await scope.DbContext.DistributedRateLimitBuckets.SingleAsync(b => b.PolicyName == "auth-ip");
        bucket.PartitionKey.Should().Be("ip:203.0.113.7");
        bucket.Count.Should().Be(30);
    }

    [Theory]
    [InlineData("::ffff:198.51.100.1", "ip:198.51.100.1")]
    [InlineData("2001:0db8:0000::1", "ip:2001:db8::1")]
    [InlineData(null, "ip:unknown")]
    [InlineData("invalid", "ip:unknown")]
    [InlineData("198.51.100.1, 198.51.100.2", "ip:unknown")]
    public async Task VerifiedBff_UsesCanonicalClientIpOrUnknown(string? forwardedIp, string expectedKey)
    {
        using var scope = new RateLimitFilterTestScope("test-forwarding-secret");
        var context = scope.CreateContext(new AuthController.SendCodeRequest("a@x.com"));
        context.HttpContext.Request.Headers["X-Orbit-Bff-Secret"] = "test-forwarding-secret";
        if (forwardedIp is not null)
            context.HttpContext.Request.Headers["X-Orbit-Client-Ip"] = forwardedIp;

        await scope.ExecuteAsync(context);

        RateLimitFilterTestScope.AssertOutcome(context, true);
        var bucket = await scope.DbContext.DistributedRateLimitBuckets.SingleAsync(b => b.PolicyName == "auth-ip");
        bucket.PartitionKey.Should().Be(expectedKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedDimension_SetsRetryWindow_AndBothDimensionsAreEvaluated(bool rejectIp)
    {
        _service.TryAcquireAsync("auth", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(rejectIp, 10, 10, DateTime.UtcNow.AddMinutes(2)));
        _service.TryAcquireAsync("auth-ip", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(!rejectIp, 30, 30, DateTime.UtcNow.AddMinutes(5)));
        var filter = new DistributedRateLimitFilter("auth", _service, _authSessionService, new BffClientIpResolver(Options.Create(new BffForwardingSettings())), _logger);
        var (context, http) = CreateExecutingContext(new AuthController.SendCodeRequest("a@x.com"));
        http.TraceIdentifier = "req_composite";
        var nextCalled = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(CreateExecutedContext(context));
        });

        nextCalled.Should().BeFalse();
        RateLimitFilterTestScope.AssertOutcome(context, false);
        var retryAfter = int.Parse(http.Response.Headers.RetryAfter.ToString());
        retryAfter.Should().BeInRange(rejectIp ? 297 : 117, rejectIp ? 300 : 120);
        http.Response.Headers["X-Orbit-Request-Id"].ToString().Should().Be("req_composite");
        await _service.Received(1).TryAcquireAsync("auth", "auth:email:a@x.com", Arg.Any<CancellationToken>());
        await _service.Received(1).TryAcquireAsync("auth-ip", "ip:unknown", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IpDimension_StoreFailure_FailsClosed()
    {
        _service.TryAcquireAsync("auth", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(true, 10, 1, DateTime.UtcNow.AddMinutes(1)));
        _service.TryAcquireAsync("auth-ip", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("rate-limit store down"));
        var filter = new DistributedRateLimitFilter("auth", _service, _authSessionService, new BffClientIpResolver(Options.Create(new BffForwardingSettings())), _logger);
        var (context, _) = CreateExecutingContext(new AuthController.SendCodeRequest("a@x.com"));
        var nextCalled = false;

        var act = () => filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(CreateExecutedContext(context));
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        nextCalled.Should().BeFalse();
    }

    private void SetupAllowedLease(Guid userId)
    {
        _service.TryAcquireLeaseAsync(
                ConcurrentChatLimitFilter.PolicyName,
                $"user:{userId}",
                1,
                TimeSpan.FromMinutes(5),
                Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(true, 1, 1, DateTime.UtcNow.AddMinutes(5)));
    }

    private static (ActionExecutingContext Context, HttpContext HttpContext) CreateAuthenticatedExecutingContext(Guid userId)
    {
        var result = CreateExecutingContext();
        result.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            "test"));
        return result;
    }

    private static (ActionExecutingContext Context, HttpContext HttpContext) CreateExecutingContext(
        params object?[] actionArguments)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/api/support";

        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ControllerActionDescriptor());

        var arguments = new Dictionary<string, object?>();
        for (var index = 0; index < actionArguments.Length; index++)
            arguments[$"arg{index}"] = actionArguments[index];

        var executingContext = new ActionExecutingContext(
            actionContext,
            [],
            arguments,
            controller: new object());

        return (executingContext, httpContext);
    }

    private static ActionExecutedContext CreateExecutedContext(ActionExecutingContext executingContext)
    {
        return new ActionExecutedContext(executingContext, executingContext.Filters, executingContext.Controller);
    }
}

internal sealed class RateLimitFilterTestScope : IDisposable
{
    public OrbitDbContext DbContext { get; } = new(new DbContextOptionsBuilder<OrbitDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly ServiceProvider _provider;

    public RateLimitFilterTestScope(string? bffForwardingSecret = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:BffForwardingSecret"] = bffForwardingSecret
        });
        builder.AddOrbitRateLimiting();
        _provider = builder.Services
            .AddSingleton<IDistributedRateLimitService>(new DistributedRateLimitService(DbContext, new FrozenClock()))
            .AddSingleton(Substitute.For<IAuthSessionService>())
            .AddSingleton<ILogger<DistributedRateLimitFilter>>(NullLogger<DistributedRateLimitFilter>.Instance)
            .BuildServiceProvider();
    }

    public ActionExecutingContext CreateContext(object request, MethodInfo? action = null)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        http.Request.Method = "POST";
        http.Request.Path = action?.DeclaringType == typeof(OAuthController) ? "/oauth/verify-code" : "/api/auth/send-code";
        return new ActionExecutingContext(
            new ActionContext(http, new RouteData(), action is null
                ? new ControllerActionDescriptor()
                : new ControllerActionDescriptor { MethodInfo = action }),
            [], new Dictionary<string, object?> { ["request"] = request }, new object());
    }

    public async Task ExecuteAsync(ActionExecutingContext context, Action? accepted = null, string policy = "auth", Func<Task>? executeAction = null)
    {
        var action = (context.ActionDescriptor as ControllerActionDescriptor)?.MethodInfo;
        var attribute = action?.GetCustomAttribute<DistributedRateLimitAttribute>() ?? new DistributedRateLimitAttribute(policy);
        var filter = (IAsyncActionFilter)attribute.CreateInstance(_provider);
        await filter.OnActionExecutionAsync(context, async () =>
        {
            accepted?.Invoke();
            if (executeAction is not null)
                await executeAction();
            return new ActionExecutedContext(context, [], context.Controller);
        });
    }

    public static void AssertOutcome(ActionExecutingContext context, bool allowed)
    {
        if (allowed)
        {
            context.Result.Should().BeNull();
            context.HttpContext.Response.Headers.RetryAfter.Should().BeEmpty();
        }
        else
        {
            context.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
            int.Parse(context.HttpContext.Response.Headers.RetryAfter.ToString()).Should().BeGreaterThan(0);
        }
    }

    public void Dispose()
    {
        _provider.Dispose();
        DbContext.Dispose();
    }

    private sealed class FrozenClock : TimeProvider
    {
        private readonly DateTimeOffset _now = TimeProvider.System.GetUtcNow();
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
