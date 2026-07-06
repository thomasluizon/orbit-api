using System.Data.Common;
using System.Linq.Expressions;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Auth.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Auth;

public class VerifyCodeCommandHandlerTests
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAuthSessionService _authSessionService = Substitute.For<IAuthSessionService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly VerifyCodeCommandHandler _handler;

    private const string TestEmail = "test@example.com";

    public VerifyCodeCommandHandlerTests()
    {
        _handler = new VerifyCodeCommandHandler(
            _cache, _userRepo, _unitOfWork, _authSessionService, _emailService, _mediator,
            Substitute.For<ILogger<VerifyCodeCommandHandler>>());

        _authSessionService.CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
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
        await _userRepo.DidNotReceive().AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvalidCode_ReturnsFailureAndIncrementsAttempts()
    {
        SetupCacheWithCode("123456");

        var command = new VerifyCodeCommand(TestEmail, "999999");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Invalid");
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
        await _userRepo.Received(1).AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
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
        result.Error.Should().Contain("Too many attempts");
        result.ErrorCode.Should().Be("TOO_MANY_ATTEMPTS");
    }

    [Fact]
    public async Task Handle_ResendingCode_DoesNotResetAttemptBudget()
    {
        var sendCodeHandler = new SendCodeCommandHandler(_cache, _emailService);
        await sendCodeHandler.Handle(new SendCodeCommand(TestEmail), CancellationToken.None);

        for (var attempt = 0; attempt < 3; attempt++)
            await _handler.Handle(new VerifyCodeCommand(TestEmail, "000000"), CancellationToken.None);

        SetupCacheWithCode("654321");

        var result = await _handler.Handle(new VerifyCodeCommand(TestEmail, "654321"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("TOO_MANY_ATTEMPTS");
    }

    [Fact]
    public async Task Handle_ConcurrentFirstLogin_ResolvesToExistingUserWithout500()
    {
        SetupCacheWithCode("123456");
        var racedUser = User.Create("Raced", TestEmail).Value;
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null, racedUser);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateException("duplicate", new FakeUniqueViolationException()));

        var result = await _handler.Handle(new VerifyCodeCommand(TestEmail, "123456"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Email.Should().Be(TestEmail);
        await _userRepo.Received(1).AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
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

    private void SetupCacheWithCode(string code)
    {
        var entry = new VerificationEntry(code, 0, DateTime.UtcNow);
        _cache.Set($"verify:{TestEmail}", entry,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
    }

    private void SetupExistingUser(User user)
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
    }

    private sealed class FakeUniqueViolationException : DbException
    {
        public override string SqlState => "23505";
    }
}
