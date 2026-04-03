using System.Linq.Expressions;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Auth;

public class VerifyCodeCommandHandlerTests
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly VerifyCodeCommandHandler _handler;

    private const string TestEmail = "test@example.com";

    public VerifyCodeCommandHandlerTests()
    {
        _handler = new VerifyCodeCommandHandler(
            _cache, _userRepo, _unitOfWork, _tokenService, _emailService, _mediator,
            Substitute.For<ILogger<VerifyCodeCommandHandler>>());

        _tokenService.GenerateToken(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns("jwt-token");
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
        _cache.TryGetValue($"verify:{TestEmail}", out VerificationEntry? entry).Should().BeTrue();
        entry!.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task Handle_ExpiredCode_ReturnsFailure()
    {
        // No cache entry -- simulates expired code
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
        // No existing user -- FindOneTrackedAsync returns null by default

        var command = new VerifyCodeCommand(TestEmail, "123456");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Token.Should().Be("jwt-token");
        await _userRepo.Received(1).AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_MaxAttemptsExceeded_RemovesCacheAndReturnsFailure()
    {
        var entry = new VerificationEntry("123456", 3, DateTime.UtcNow);
        _cache.Set($"verify:{TestEmail}", entry,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });

        var command = new VerifyCodeCommand(TestEmail, "123456");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Too many attempts");
        result.ErrorCode.Should().Be("TOO_MANY_ATTEMPTS");
        _cache.TryGetValue($"verify:{TestEmail}", out _).Should().BeFalse();
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
}
