using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Auth;

public class AuthCommandHandlerTests
{
    private readonly MemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAuthSessionService _authSessionService = Substitute.For<IAuthSessionService>();

    private const string TestEmail = "test@example.com";

    // ===== SendCode =====

    [Fact]
    public async Task SendCode_Valid_CachesCodeAndSendsEmail()
    {
        var handler = new SendCodeCommandHandler(_cache, _emailService);
        var command = new SendCodeCommand(TestEmail);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // Verify code was cached
        _cache.TryGetValue($"verify:{TestEmail}", out VerificationEntry? entry).Should().BeTrue();
        entry.Should().NotBeNull();
        entry!.Code.Should().HaveLength(6);
        entry.Attempts.Should().Be(0);
        // Verify email was sent
        await _emailService.Received(1).SendVerificationCodeAsync(
            TestEmail, Arg.Any<string>(), "en", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendCode_RateLimit_ReturnsFailure()
    {
        // Pre-populate cache with a recent entry (less than 60s ago)
        var existingEntry = new VerificationEntry("123456", 0, DateTime.UtcNow);
        _cache.Set($"verify:{TestEmail}", existingEntry,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });

        var handler = new SendCodeCommandHandler(_cache, _emailService);
        var command = new SendCodeCommand(TestEmail);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("wait");
        await _emailService.DidNotReceive().SendVerificationCodeAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ===== VerifyCode =====

    [Fact]
    public async Task VerifyCode_Valid_ReturnsLoginResponse()
    {
        var user = User.Create("Test", TestEmail).Value;
        SetupCacheWithCode("123456");
        SetupExistingUser(user);
        _authSessionService.CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SessionTokens("jwt-token", "refresh-token")));

        var handler = new VerifyCodeCommandHandler(_cache, _userRepo, _unitOfWork, _authSessionService, _emailService, Substitute.For<MediatR.IMediator>(), Substitute.For<ILogger<VerifyCodeCommandHandler>>());
        var command = new VerifyCodeCommand(TestEmail, "123456");

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Token.Should().Be("jwt-token");
        result.Value.Email.Should().Be(TestEmail);
        // Cache should be cleared after successful verification
        _cache.TryGetValue($"verify:{TestEmail}", out _).Should().BeFalse();
    }

    [Fact]
    public async Task VerifyCode_WrongCode_ReturnsFailure()
    {
        SetupCacheWithCode("123456");

        var handler = new VerifyCodeCommandHandler(_cache, _userRepo, _unitOfWork, _authSessionService, _emailService, Substitute.For<MediatR.IMediator>(), Substitute.For<ILogger<VerifyCodeCommandHandler>>());
        var command = new VerifyCodeCommand(TestEmail, "999999");

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Invalid");
        // Attempt count should have incremented
        _cache.TryGetValue($"verify:{TestEmail}", out VerificationEntry? entry).Should().BeTrue();
        entry!.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task VerifyCode_MaxAttempts_ReturnsFailure()
    {
        // Set up entry with 3 attempts already (max reached)
        var entry = new VerificationEntry("123456", 3, DateTime.UtcNow);
        _cache.Set($"verify:{TestEmail}", entry,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });

        var handler = new VerifyCodeCommandHandler(_cache, _userRepo, _unitOfWork, _authSessionService, _emailService, Substitute.For<MediatR.IMediator>(), Substitute.For<ILogger<VerifyCodeCommandHandler>>());
        var command = new VerifyCodeCommand(TestEmail, "123456");

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Too many attempts");
        // Cache entry should be removed
        _cache.TryGetValue($"verify:{TestEmail}", out _).Should().BeFalse();
    }

    [Fact]
    public async Task VerifyCode_NewUser_CreatesAccount()
    {
        SetupCacheWithCode("123456");
        // No existing user - FindOneTrackedAsync returns null by default with NSubstitute
        _authSessionService.CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SessionTokens("jwt-token", "refresh-token")));

        var handler = new VerifyCodeCommandHandler(_cache, _userRepo, _unitOfWork, _authSessionService, _emailService, Substitute.For<MediatR.IMediator>(), Substitute.For<ILogger<VerifyCodeCommandHandler>>());
        var command = new VerifyCodeCommand(TestEmail, "123456");

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _userRepo.Received(1).AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyCode_ExistingUser_ReturnsToken()
    {
        var user = User.Create("Existing", TestEmail).Value;
        SetupCacheWithCode("123456");
        SetupExistingUser(user);
        _authSessionService.CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SessionTokens("jwt-token", "refresh-token")));

        var handler = new VerifyCodeCommandHandler(_cache, _userRepo, _unitOfWork, _authSessionService, _emailService, Substitute.For<MediatR.IMediator>(), Substitute.For<ILogger<VerifyCodeCommandHandler>>());
        var command = new VerifyCodeCommand(TestEmail, "123456");

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Token.Should().Be("jwt-token");
        // Should NOT create a new user
        await _userRepo.DidNotReceive().AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    // ----- Helpers -----

    private void SetupCacheWithCode(string code)
    {
        var entry = new VerificationEntry(code, 0, DateTime.UtcNow);
        _cache.Set($"verify:{TestEmail}", entry,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
    }

    private void SetupExistingUser(User user)
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
    }
}
