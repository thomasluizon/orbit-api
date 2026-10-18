using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Orbit.Application.Auth.Jobs;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Application.Common;

namespace Orbit.Application.Tests.Commands.Auth;

public class VerifyCodeCommandHandlerTests
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly IAuthUserStore _userRepo = Substitute.For<IAuthUserStore>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAuthSessionService _authSessionService = Substitute.For<IAuthSessionService>();
    private readonly IBackgroundJobClient _jobs = Substitute.For<IBackgroundJobClient>();
    private readonly VerifyCodeCommandHandler _handler;

    private const string TestEmail = "test@example.com";

    public VerifyCodeCommandHandlerTests()
    {
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<Orbit.Application.Auth.Queries.LoginResponse>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<Result<Orbit.Application.Auth.Queries.LoginResponse>>>>()(call.Arg<CancellationToken>()));
        _handler = new VerifyCodeCommandHandler(
            _cache, _userRepo, _unitOfWork, _authSessionService, _jobs);

        _userRepo.UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<User>());
        _authSessionService.CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SessionTokens("jwt-token", "refresh-token")));
    }

    [Fact]
    public async Task Handle_ValidCode_ExistingUser_ReturnsLoginResponse()
    {
        var user = User.Create("Existing User", TestEmail).Value;
        SetupCacheWithCode("123456");
        SetupExistingUser(user);

        var command = new VerifyCodeCommand(TestEmail, "123456");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Token.Should().Be("jwt-token");
        result.Value.Email.Should().Be(TestEmail);
        _cache.TryGetValue($"verify:{TestEmail}", out _).Should().BeFalse();
        await _userRepo.Received(1).UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvalidCode_ReturnsFailureAndIncrementsAttempts()
    {
        SetupCacheWithCode("123456");

        var command = new VerifyCodeCommand(TestEmail, "999999");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.InvalidVerificationCode.Message);
        result.ErrorCode.Should().Be("INVALID_VERIFICATION_CODE");
        _cache.TryGetValue($"verify-attempts:{TestEmail}", out int attempts).Should().BeTrue();
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task Handle_ExpiredCode_ReturnsFailure()
    {
        var command = new VerifyCodeCommand(TestEmail, "123456");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("expired");
        result.ErrorCode.Should().Be("CODE_EXPIRED");
    }

    [Fact]
    public async Task Handle_NewUser_CreatesAccountAndReturnsToken()
    {
        SetupCacheWithCode("123456");

        var command = new VerifyCodeCommand(TestEmail, "123456");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Token.Should().Be("jwt-token");
        await _userRepo.Received(1).UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_MaxAttemptsExceeded_ReturnsFailure()
    {
        SetupCacheWithCode("123456");
        for (var attempt = 0; attempt < 3; attempt++)
            await _handler.Handle(new VerifyCodeCommand(TestEmail, "999999"), CancellationToken.None);

        var result = await _handler.Handle(new VerifyCodeCommand(TestEmail, "123456"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.TooManyCodeAttempts.Message);
        result.ErrorCode.Should().Be("TOO_MANY_ATTEMPTS");
    }

    [Fact]
    public async Task Handle_ResendingCode_DoesNotResetAttemptBudget()
    {
        var sendCodeHandler = new SendCodeCommandHandler(_cache, Substitute.For<IBackgroundJobClient>());
        await sendCodeHandler.Handle(new SendCodeCommand(TestEmail), CancellationToken.None);

        for (var attempt = 0; attempt < 3; attempt++)
            await _handler.Handle(new VerifyCodeCommand(TestEmail, "000000"), CancellationToken.None);

        SetupCacheWithCode("654321");

        var result = await _handler.Handle(new VerifyCodeCommand(TestEmail, "654321"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("TOO_MANY_ATTEMPTS");
    }

    [Fact]
    public async Task Handle_DeactivatedUser_ReactivatesAndSaves()
    {
        var user = User.Create("Deactivated", TestEmail).Value;
        user.Deactivate(DateTime.UtcNow.AddDays(7));
        SetupCacheWithCode("123456");
        SetupExistingUser(user);

        var command = new VerifyCodeCommand(TestEmail, "123456");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.WasReactivated.Should().BeTrue();
        user.IsDeactivated.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_AlwaysEnqueuesSignupDecision(bool exists)
    {
        SetupCacheWithCode("123456");
        if (exists)
            SetupExistingUser(User.Create("Existing", TestEmail).Value);

        var result = await _handler.Handle(new VerifyCodeCommand(TestEmail, "123456", "pt-BR", "friend"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _jobs.Received(1).Create(Arg.Is<Job>(job => job.Type == typeof(CompleteSignupJob)
            && (Guid)job.Args[0] == result.Value.UserId && (bool)job.Args[1] == !exists
            && (string)job.Args[2] == "pt-BR" && (string)job.Args[3] == "friend"), Arg.Any<IState>());
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("expired")]
    [InlineData("wrong")]
    [InlineData("throttled")]
    public async Task Handle_AnonymousFailure_NeverTouchesPersistence(string mode)
    {
        var failures = new List<(string Error, string? Code, int Attempts)>();
        foreach (var exists in new[] { true, false })
        {
            _cache.Remove($"verify:{TestEmail}");
            _cache.Remove($"verify-attempts:{TestEmail}");
            if (exists)
                SetupExistingUser(User.Create("Existing", TestEmail).Value);
            else
                _userRepo.UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<User>());
            if (mode != "absent")
                SetupCacheWithCode("123456");
            if (mode == "expired")
                _cache.Set($"verify:{TestEmail}", new VerificationEntry("123456", 0, DateTime.UtcNow),
                    new MemoryCacheEntryOptions { AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(-1) });
            if (mode == "throttled")
                _cache.Set($"verify-attempts:{TestEmail}", 3);

            var result = await _handler.Handle(new VerifyCodeCommand(TestEmail, "999999"), CancellationToken.None);

            result.IsFailure.Should().BeTrue();
            _cache.TryGetValue($"verify-attempts:{TestEmail}", out int attempts);
            failures.Add((result.Error, result.ErrorCode, attempts));
        }

        failures[0].Should().Be(failures[1]);
        failures[0].Attempts.Should().Be(mode == "wrong" ? 1 : mode == "throttled" ? 3 : 0);
        _userRepo.ReceivedCalls().Should().BeEmpty();
        _unitOfWork.ReceivedCalls().Should().BeEmpty();
        _authSessionService.ReceivedCalls().Should().BeEmpty();
        _jobs.ReceivedCalls().Should().BeEmpty();
    }

    private void SetupCacheWithCode(string code)
    {
        var entry = new VerificationEntry(code, 0, DateTime.UtcNow);
        _cache.Set($"verify:{TestEmail}", entry,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
    }

    private void SetupExistingUser(User user)
    {
        _userRepo.UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(user);
    }
}
