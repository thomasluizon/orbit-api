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
        _cache.TryGetValue($"delete-attempts:{TestEmail}", out int attempts).Should().BeTrue();
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task Handle_AttemptBudgetSurvivesResend_LocksAfterMaxAcrossNewCodes()
    {
        var user = User.Create("Test", TestEmail).Value;
        SetupUser(user);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            SetupDeletionCode(TestEmail, "123456");
            var wrong = await _handler.Handle(new ConfirmAccountDeletionCommand(UserId, "999999"), CancellationToken.None);
            wrong.Error.Should().Contain("Invalid");
        }

        SetupDeletionCode(TestEmail, "123456");
        var result = await _handler.Handle(new ConfirmAccountDeletionCommand(UserId, "123456"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Too many attempts");
        user.IsDeactivated.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
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

        var command = new ConfirmAccountDeletionCommand(UserId, "123456");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("expired");
    }

    [Fact]
    public async Task Handle_MaxAttemptsReached_ReturnsFailureWithoutDeactivating()
    {
        var user = User.Create("Test", TestEmail).Value;
        SetupUser(user);
        SetupDeletionCode(TestEmail, "123456");

        _cache.Set($"delete-attempts:{TestEmail}", 3,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15) });

        var command = new ConfirmAccountDeletionCommand(UserId, "123456");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Too many attempts");
        user.IsDeactivated.Should().BeFalse();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
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
