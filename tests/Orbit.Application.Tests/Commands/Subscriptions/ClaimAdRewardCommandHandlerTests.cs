using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Subscriptions;

public class ClaimAdRewardCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly ClaimAdRewardCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public ClaimAdRewardCommandHandlerTests()
    {
        _handler = new ClaimAdRewardCommandHandler(_userRepo, _unitOfWork, _payGate);
        _payGate.GetAiMessageLimit(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(25);
    }

    [Fact]
    public async Task Handle_ValidClaim_GrantsRewardAndReturnsResponse()
    {
        var user = User.Create("Test", "test@example.com").Value;
        // Expire the trial so GrantAdReward succeeds (Pro/trial users cannot claim)
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        SetupExistingUser(user);

        var command = new ClaimAdRewardCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.BonusMessagesGranted.Should().Be(5);
        result.Value.NewLimit.Should().Be(25);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        // FindOneTrackedAsync returns null by default
        var command = new ClaimAdRewardCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
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
