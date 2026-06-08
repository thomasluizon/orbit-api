using FluentAssertions;
using NSubstitute;
using Orbit.Application.Subscriptions.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.Subscriptions;

public class GetSubscriptionStatusQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly GetSubscriptionStatusQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetSubscriptionStatusQueryHandlerTests()
    {
        _handler = new GetSubscriptionStatusQueryHandler(_userRepo, _payGate);
    }

    private static User CreateTestUser()
    {
        return User.Create("Test User", "test@example.com").Value;
    }

    [Fact]
    public async Task Handle_UserFound_ReturnsStatus()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(500);

        var query = new GetSubscriptionStatusQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Plan.Should().Be("pro");
        result.Value.HasProAccess.Should().BeTrue();
        result.Value.IsTrialActive.Should().BeTrue();
        result.Value.AiMessagesUsed.Should().Be(0);
        result.Value.AiMessagesLimit.Should().Be(500);
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var query = new GetSubscriptionStatusQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
        result.ErrorCode.Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_PlaySubscription_ReturnsPlaySource()
    {
        var user = CreateTestUser();
        user.SetPlaySubscription("tok_123", DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(500);

        var result = await _handler.Handle(new GetSubscriptionStatusQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Source.Should().Be("play");
        result.Value.SubscriptionInterval.Should().Be("monthly");
    }

    [Fact]
    public async Task Handle_TrialUser_ReturnsTrialActive()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(500);

        var query = new GetSubscriptionStatusQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsTrialActive.Should().BeTrue();
        result.Value.HasProAccess.Should().BeTrue();
        result.Value.Plan.Should().Be("pro");
    }
}
