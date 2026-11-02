using Orbit.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
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
    private readonly IBackgroundJobClient _backgroundJobClient = Substitute.For<IBackgroundJobClient>();
    private readonly SendCodeCommandHandler _handler;
    private readonly Microsoft.Extensions.Hosting.IHostEnvironment _environment = ChallengeTestDatabase.Environment();
    private Job? _enqueuedJob;

    private const string SmokeEmail = "smoke@useorbit.org";
    private const string SmokeCode = "428913";

    public SendCodeCommandHandlerTests()
    {
        _backgroundJobClient.Create(Arg.Do<Job>(job => _enqueuedJob = job), Arg.Any<IState>());
        _handler = new SendCodeCommandHandler(ChallengeTestDatabase.For(_cache).Store(), _environment, _backgroundJobClient);
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Staging", true)]
    [InlineData("Production", false)]
    [InlineData("", false)]
    public async Task TestAccountCode_IsLimitedToDevelopmentAndStaging(string environmentName, bool fixedCodeAllowed)
    {
        var previous = Environment.GetEnvironmentVariable("TEST_ACCOUNTS");
        try
        {
            Environment.SetEnvironmentVariable("TEST_ACCOUNTS", "review@example.com:123456");
            await WithEnv(environmentName, null, null, async () =>
            {
                (await _handler.Handle(new SendCodeCommand("review@example.com"), CancellationToken.None)).IsSuccess.Should().BeTrue();
                if (fixedCodeAllowed)
                {
                    (await ChallengeTestDatabase.For(_cache).Store().ConfirmAsync(
                        Orbit.Application.Auth.Services.EmailChallengeOperation.SignIn, "review@example.com", "123456")).IsSuccess.Should().BeTrue();
                    _backgroundJobClient.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
                }
                else
                    _backgroundJobClient.Received(1).Create(Arg.Any<Job>(), Arg.Any<IState>());
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_ACCOUNTS", previous);
        }
    }

    [Fact]
    public async Task SignInCode_SurvivesAFreshServiceInstance()
    {
        await WithEnv("Production", SmokeEmail, SmokeCode, async () =>
        {
            await _handler.Handle(new SendCodeCommand(SmokeEmail), CancellationToken.None);
            var freshVerifier = BuildVerifyHandler();
            var result = await freshVerifier.Handle(new VerifyCodeCommand(SmokeEmail, SmokeCode), CancellationToken.None);
            result.IsSuccess.Should().BeTrue("a fresh context must preserve an unexpired sign-in code");
        });
    }

    [Fact]
    public async Task Production_PinnedEmail_SeedsFixedCode_AndDoesNotEnqueueEmail()
    {
        await WithEnv("Production", SmokeEmail, SmokeCode, async () =>
        {
            var result = await _handler.Handle(new SendCodeCommand(SmokeEmail), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            var entry = ChallengeTestDatabase.For(_cache).Row($"verify:{SmokeEmail}");
            entry.Should().NotBeNull();
            entry!.CodeHash.Should().HaveCount(32);
            System.Text.Encoding.UTF8.GetString(entry!.CodeHash).Should().NotContain(SmokeCode);
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
            ChallengeTestDatabase.For(_cache).Row($"verify:someone-else@useorbit.org").Should().NotBeNull();
            var code = (string)_enqueuedJob!.Args[1];
            code.Should().NotBe(SmokeCode);
            AssertEnqueuedVerificationEmail("someone-else@useorbit.org", code, "en");
        });
    }

    [Fact]
    public async Task NonProduction_PinnedEmail_NoBypass_EnqueuesEmail()
    {
        await WithEnv("Development", SmokeEmail, SmokeCode, async () =>
        {
            var result = await _handler.Handle(new SendCodeCommand(SmokeEmail), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            ChallengeTestDatabase.For(_cache).Row($"verify:{SmokeEmail}").Should().NotBeNull();
            var code = (string)_enqueuedJob!.Args[1];
            code.Should().NotBe(SmokeCode);
            AssertEnqueuedVerificationEmail(SmokeEmail, code, "en");
        });
    }

    [Fact]
    public async Task NonDefaultLanguage_EnqueuesEmailWithThatLanguage()
    {
        await WithEnv("Development", SmokeEmail, SmokeCode, async () =>
        {
            var result = await _handler.Handle(new SendCodeCommand(SmokeEmail, "pt-BR"), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            ChallengeTestDatabase.For(_cache).Row($"verify:{SmokeEmail}").Should().NotBeNull();
            var code = (string)_enqueuedJob!.Args[1];
            AssertEnqueuedVerificationEmail(SmokeEmail, code, "pt-BR");
        });
    }

    [Fact]
    public async Task Production_UnsetSecret_NoBypass_EnqueuesEmail()
    {
        await WithEnv("Production", SmokeEmail, null, async () =>
        {
            var result = await _handler.Handle(new SendCodeCommand(SmokeEmail), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            ChallengeTestDatabase.For(_cache).Row($"verify:{SmokeEmail}").Should().NotBeNull();
            var code = (string)_enqueuedJob!.Args[1];
            code.Should().NotBe(SmokeCode);
            AssertEnqueuedVerificationEmail(SmokeEmail, code, "en");
        });
    }

    [Fact]
    public async Task Production_ShortSmokeCode_NoBypass_EnqueuesEmail()
    {
        await WithEnv("Production", SmokeEmail, "short", async () =>
        {
            var result = await _handler.Handle(new SendCodeCommand(SmokeEmail), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            ChallengeTestDatabase.For(_cache).Row($"verify:{SmokeEmail}").Should().NotBeNull();
            var code = (string)_enqueuedJob!.Args[1];
            code.Should().NotBe("short");
            AssertEnqueuedVerificationEmail(SmokeEmail, code, "en");
        });
    }

    [Fact]
    public async Task Production_NonNumericSmokeCode_NoBypass_EnqueuesEmail()
    {
        await WithEnv("Production", SmokeEmail, "abc123", async () =>
        {
            var result = await _handler.Handle(new SendCodeCommand(SmokeEmail), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            ChallengeTestDatabase.For(_cache).Row($"verify:{SmokeEmail}").Should().NotBeNull();
            var code = (string)_enqueuedJob!.Args[1];
            code.Should().NotBe("abc123");
            AssertEnqueuedVerificationEmail(SmokeEmail, code, "en");
        });
    }

    [Fact]
    public async Task Cooldown_WithinWindow_ReturnsFailure_AndDoesNotEnqueue()
    {
        await WithEnv("Development", SmokeEmail, SmokeCode, async () =>
        {
            ChallengeTestDatabase.For(_cache).Seed($"verify:{SmokeEmail}", "111111");

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
        var userRepo = Substitute.For<IAuthUserStore>();
        userRepo.UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<User>());
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<Orbit.Application.Auth.Queries.LoginResponse>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<Result<Orbit.Application.Auth.Queries.LoginResponse>>>>()(call.Arg<CancellationToken>()));
        var authSessionService = Substitute.For<IAuthSessionService>();

        authSessionService.CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SessionTokens("jwt-token", "refresh-token")));

        return new VerifyCodeCommandHandler(
            ChallengeTestDatabase.For(_cache).Store(), userRepo, unitOfWork, authSessionService, new SignupJobDispatcher(_backgroundJobClient, new HttpContextAccessor(), NullLogger<SignupJobDispatcher>.Instance), _backgroundJobClient, new AccountReactivation(Substitute.For<IGenericRepository<OwedSubscriptionCancellation>>()));
    }

    private async Task WithEnv(string aspNetEnv, string? smokeEmail, string? smokeCode, Func<Task> body)
    {
        _environment.EnvironmentName.Returns(aspNetEnv);
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
