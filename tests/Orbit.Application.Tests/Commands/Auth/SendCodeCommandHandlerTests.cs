using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Jobs;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Auth;

[Collection("ProcessEnvironment")]
public class SendCodeCommandHandlerTests
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IBackgroundJobClient _backgroundJobClient = Substitute.For<IBackgroundJobClient>();
    private readonly SendCodeCommandHandler _handler;
    private Job? _enqueuedJob;

    private const string SmokeEmail = "smoke@useorbit.org";
    private const string SmokeCode = "428913";

    public SendCodeCommandHandlerTests()
    {
        _backgroundJobClient.Create(Arg.Do<Job>(job => _enqueuedJob = job), Arg.Any<IState>());
        _handler = new SendCodeCommandHandler(_cache, _backgroundJobClient);
    }

    [Fact]
    public async Task Production_PinnedEmail_SeedsFixedCode_AndDoesNotEnqueueEmail()
    {
        await WithEnv("Production", SmokeEmail, SmokeCode, async () =>
        {
            var result = await _handler.Handle(new SendCodeCommand(SmokeEmail), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            _cache.TryGetValue($"verify:{SmokeEmail}", out VerificationEntry? entry).Should().BeTrue();
            entry!.Code.Should().Be(SmokeCode);
            _backgroundJobClient.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
        });
    }

    [Fact]
    public async Task Production_PinnedEmail_WrongSubmittedCode_VerifyFails()
    {
        await WithEnv("Production", SmokeEmail, SmokeCode, async () =>
        {
            await _handler.Handle(new SendCodeCommand(SmokeEmail), CancellationToken.None);

            var verify = BuildVerifyHandler();
            var result = await verify.Handle(new VerifyCodeCommand(SmokeEmail, "000000"), CancellationToken.None);

            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be("INVALID_VERIFICATION_CODE");
        });
    }

    [Fact]
    public async Task Production_PinnedEmail_CorrectSubmittedCode_VerifySucceeds()
    {
        await WithEnv("Production", SmokeEmail, SmokeCode, async () =>
        {
            await _handler.Handle(new SendCodeCommand(SmokeEmail), CancellationToken.None);

            var verify = BuildVerifyHandler();
            var result = await verify.Handle(new VerifyCodeCommand(SmokeEmail, SmokeCode), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            result.Value.Email.Should().Be(SmokeEmail);
        });
    }

    [Fact]
    public async Task Production_DifferentEmail_NoBypass_EnqueuesEmailWithCachedCode()
    {
        await WithEnv("Production", SmokeEmail, SmokeCode, async () =>
        {
            var result = await _handler.Handle(new SendCodeCommand("someone-else@useorbit.org"), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            _cache.TryGetValue($"verify:someone-else@useorbit.org", out VerificationEntry? entry).Should().BeTrue();
            entry!.Code.Should().NotBe(SmokeCode);
            AssertEnqueuedVerificationEmail("someone-else@useorbit.org", entry.Code, "en");
        });
    }

    [Fact]
    public async Task NonProduction_PinnedEmail_NoBypass_EnqueuesEmail()
    {
        await WithEnv("Development", SmokeEmail, SmokeCode, async () =>
        {
            var result = await _handler.Handle(new SendCodeCommand(SmokeEmail), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            _cache.TryGetValue($"verify:{SmokeEmail}", out VerificationEntry? entry).Should().BeTrue();
            entry!.Code.Should().NotBe(SmokeCode);
            AssertEnqueuedVerificationEmail(SmokeEmail, entry.Code, "en");
        });
    }

    [Fact]
    public async Task NonDefaultLanguage_EnqueuesEmailWithThatLanguage()
    {
        await WithEnv("Development", SmokeEmail, SmokeCode, async () =>
        {
            var result = await _handler.Handle(new SendCodeCommand(SmokeEmail, "pt-BR"), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            _cache.TryGetValue($"verify:{SmokeEmail}", out VerificationEntry? entry).Should().BeTrue();
            AssertEnqueuedVerificationEmail(SmokeEmail, entry!.Code, "pt-BR");
        });
    }

    [Fact]
    public async Task Production_UnsetSecret_NoBypass_EnqueuesEmail()
    {
        await WithEnv("Production", SmokeEmail, null, async () =>
        {
            var result = await _handler.Handle(new SendCodeCommand(SmokeEmail), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            _cache.TryGetValue($"verify:{SmokeEmail}", out VerificationEntry? entry).Should().BeTrue();
            entry!.Code.Should().NotBe(SmokeCode);
            AssertEnqueuedVerificationEmail(SmokeEmail, entry.Code, "en");
        });
    }

    [Fact]
    public async Task Production_ShortSmokeCode_NoBypass_EnqueuesEmail()
    {
        await WithEnv("Production", SmokeEmail, "short", async () =>
        {
            var result = await _handler.Handle(new SendCodeCommand(SmokeEmail), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            _cache.TryGetValue($"verify:{SmokeEmail}", out VerificationEntry? entry).Should().BeTrue();
            entry!.Code.Should().NotBe("short");
            AssertEnqueuedVerificationEmail(SmokeEmail, entry.Code, "en");
        });
    }

    [Fact]
    public async Task Production_NonNumericSmokeCode_NoBypass_EnqueuesEmail()
    {
        await WithEnv("Production", SmokeEmail, "abc123", async () =>
        {
            var result = await _handler.Handle(new SendCodeCommand(SmokeEmail), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            _cache.TryGetValue($"verify:{SmokeEmail}", out VerificationEntry? entry).Should().BeTrue();
            entry!.Code.Should().NotBe("abc123");
            AssertEnqueuedVerificationEmail(SmokeEmail, entry.Code, "en");
        });
    }

    [Fact]
    public async Task Cooldown_WithinWindow_ReturnsFailure_AndDoesNotEnqueue()
    {
        await WithEnv("Development", SmokeEmail, SmokeCode, async () =>
        {
            _cache.Set($"verify:{SmokeEmail}", new VerificationEntry("111111", 0, DateTime.UtcNow),
                new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });

            var result = await _handler.Handle(new SendCodeCommand(SmokeEmail), CancellationToken.None);

            result.IsFailure.Should().BeTrue();
            _backgroundJobClient.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
        });
    }

    private void AssertEnqueuedVerificationEmail(string expectedEmail, string expectedCode, string expectedLanguage)
    {
        _backgroundJobClient.Received(1).Create(
            Arg.Any<Job>(), Arg.Is<IState>(state => state is EnqueuedState));
        _enqueuedJob.Should().NotBeNull();
        _enqueuedJob!.Type.Should().Be<SendVerificationCodeEmailJob>();
        _enqueuedJob.Method.Name.Should().Be(nameof(SendVerificationCodeEmailJob.ExecuteAsync));
        _enqueuedJob.Args.Should().Equal(expectedEmail, expectedCode, expectedLanguage);
    }

    private VerifyCodeCommandHandler BuildVerifyHandler()
    {
        var userRepo = Substitute.For<IGenericRepository<User>>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var authSessionService = Substitute.For<IAuthSessionService>();
        var mediator = Substitute.For<IMediator>();

        authSessionService.CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SessionTokens("jwt-token", "refresh-token")));

        return new VerifyCodeCommandHandler(
            _cache, userRepo, unitOfWork, authSessionService, _emailService, mediator,
            Substitute.For<ILogger<VerifyCodeCommandHandler>>());
    }

    private static async Task WithEnv(string aspNetEnv, string? smokeEmail, string? smokeCode, Func<Task> body)
    {
        var priorEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var priorEmail = Environment.GetEnvironmentVariable("SMOKE_TEST_EMAIL");
        var priorCode = Environment.GetEnvironmentVariable("SMOKE_TEST_CODE");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", aspNetEnv);
        Environment.SetEnvironmentVariable("SMOKE_TEST_EMAIL", smokeEmail);
        Environment.SetEnvironmentVariable("SMOKE_TEST_CODE", smokeCode);
        try
        {
            await body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", priorEnv);
            Environment.SetEnvironmentVariable("SMOKE_TEST_EMAIL", priorEmail);
            Environment.SetEnvironmentVariable("SMOKE_TEST_CODE", priorCode);
        }
    }
}
