using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Auth;

public class ConfirmAccountDeletionCommandHandlerTests
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ConfirmAccountDeletionCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private const string TestEmail = "test@example.com";

    public ConfirmAccountDeletionCommandHandlerTests()
    {
        _handler = new ConfirmAccountDeletionCommandHandler(_cache, _userRepo, _unitOfWork);
    }

    [Fact]
    public async Task Handle_ValidCode_DeactivatesUserAndReturnsScheduledDate()
    {
        var user = User.Create("Test", TestEmail).Value;
        SetupUser(user);
        SetupDeletionCode(TestEmail, "123456");

        var command = new ConfirmAccountDeletionCommand(UserId, "123456");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeAfter(DateTime.UtcNow);
        user.IsDeactivated.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        // Cache entry should be cleared after successful verification
        _cache.TryGetValue($"delete:{TestEmail}", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_InvalidCode_ReturnsFailureAndIncrementsAttempts()
    {
        var user = User.Create("Test", TestEmail).Value;
        SetupUser(user);
        SetupDeletionCode(TestEmail, "123456");

        var command = new ConfirmAccountDeletionCommand(UserId, "999999");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Invalid");
        user.IsDeactivated.Should().BeFalse();
        // Attempt should be incremented
        _cache.TryGetValue($"delete:{TestEmail}", out VerificationEntry? entry).Should().BeTrue();
        entry!.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        // GetByIdAsync returns null by default
        var command = new ConfirmAccountDeletionCommand(UserId, "123456");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task Handle_ExpiredCode_ReturnsFailure()
    {
        var user = User.Create("Test", TestEmail).Value;
        SetupUser(user);
        // No cache entry -- simulates expired code

        var command = new ConfirmAccountDeletionCommand(UserId, "123456");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("expired");
    }

    [Fact]
    public async Task Handle_MaxAttemptsReached_RemovesCacheAndReturnsFailure()
    {
        var user = User.Create("Test", TestEmail).Value;
        SetupUser(user);

        var entry = new VerificationEntry("123456", 3, DateTime.UtcNow);
        _cache.Set($"delete:{TestEmail}", entry,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });

        var command = new ConfirmAccountDeletionCommand(UserId, "123456");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Too many attempts");
        _cache.TryGetValue($"delete:{TestEmail}", out _).Should().BeFalse();
    }

    private void SetupUser(User user)
    {
        _userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(user);
    }

    private void SetupDeletionCode(string email, string code)
    {
        var entry = new VerificationEntry(code, 0, DateTime.UtcNow);
        _cache.Set($"delete:{email.ToLowerInvariant()}", entry,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });
    }
}
