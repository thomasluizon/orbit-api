using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Auth.Commands;
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
    private readonly SendCodeCommandHandler _handler;

    private const string SmokeEmail = "smoke@useorbit.org";
    private const string SmokeCode = "428913";

    public SendCodeCommandHandlerTests()
    {
        _handler = new SendCodeCommandHandler(_cache, _emailService);
    }

    [Fact]
    public async Task Production_PinnedEmail_SeedsFixedCode_AndSkipsEmail()
    {
        await WithEnv("Production", SmokeEmail, SmokeCode, async () =>
        {
            var result = await _handler.Handle(new SendCodeCommand(SmokeEmail), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            _cache.TryGetValue($"verify:{SmokeEmail}", out VerificationEntry? entry).Should().BeTrue();
            entry!.Code.Should().Be(SmokeCode);
            await _emailService.DidNotReceive().SendVerificationCodeAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
    public async Task Production_DifferentEmail_NoBypass_SendsEmail()
    {
        await WithEnv("Production", SmokeEmail, SmokeCode, async () =>
        {
            var result = await _handler.Handle(new SendCodeCommand("someone-else@useorbit.org"), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            _cache.TryGetValue($"verify:someone-else@useorbit.org", out VerificationEntry? entry).Should().BeTrue();
            entry!.Code.Should().NotBe(SmokeCode);
            await _emailService.Received(1).SendVerificationCodeAsync(
                "someone-else@useorbit.org", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task NonProduction_PinnedEmail_NoBypass_SendsEmail()
    {
        await WithEnv("Development", SmokeEmail, SmokeCode, async () =>
        {
            var result = await _handler.Handle(new SendCodeCommand(SmokeEmail), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            _cache.TryGetValue($"verify:{SmokeEmail}", out VerificationEntry? entry).Should().BeTrue();
            entry!.Code.Should().NotBe(SmokeCode);
            await _emailService.Received(1).SendVerificationCodeAsync(
                SmokeEmail, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Production_UnsetSecret_NoBypass_SendsEmail()
    {
        await WithEnv("Production", SmokeEmail, null, async () =>
        {
            var result = await _handler.Handle(new SendCodeCommand(SmokeEmail), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            _cache.TryGetValue($"verify:{SmokeEmail}", out VerificationEntry? entry).Should().BeTrue();
            entry!.Code.Should().NotBe(SmokeCode);
            await _emailService.Received(1).SendVerificationCodeAsync(
                SmokeEmail, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Production_ShortSmokeCode_NoBypass_SendsEmail()
    {
        await WithEnv("Production", SmokeEmail, "short", async () =>
        {
            var result = await _handler.Handle(new SendCodeCommand(SmokeEmail), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            _cache.TryGetValue($"verify:{SmokeEmail}", out VerificationEntry? entry).Should().BeTrue();
            entry!.Code.Should().NotBe("short");
            await _emailService.Received(1).SendVerificationCodeAsync(
                SmokeEmail, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Production_NonNumericSmokeCode_NoBypass_SendsEmail()
    {
        await WithEnv("Production", SmokeEmail, "abc123", async () =>
        {
            var result = await _handler.Handle(new SendCodeCommand(SmokeEmail), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            _cache.TryGetValue($"verify:{SmokeEmail}", out VerificationEntry? entry).Should().BeTrue();
            entry!.Code.Should().NotBe("abc123");
            await _emailService.Received(1).SendVerificationCodeAsync(
                SmokeEmail, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        });
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
