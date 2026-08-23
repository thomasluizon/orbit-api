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
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly GetSubscriptionStatusQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 8, 23);

    public GetSubscriptionStatusQueryHandlerTests()
    {
        _handler = new GetSubscriptionStatusQueryHandler(_userRepo, _payGate, _userDateService);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
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
        _payGate.GetAiMessageLimit(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(50);

        var query = new GetSubscriptionStatusQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Plan.Should().Be("pro");
        result.Value.HasProAccess.Should().BeTrue();
        result.Value.IsTrialActive.Should().BeTrue();
        result.Value.AiMessagesUsed.Should().Be(0);
        result.Value.AiMessagesLimit.Should().Be(50);
        result.Value.LapseReason.Should().BeNull();
        result.Value.SubscriptionEndedAtUtc.Should().BeNull();
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
        _payGate.GetAiMessageLimit(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(50);

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
        _payGate.GetAiMessageLimit(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(50);

        var query = new GetSubscriptionStatusQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsTrialActive.Should().BeTrue();
        result.Value.HasProAccess.Should().BeTrue();
        result.Value.Plan.Should().Be("pro");
    }

    [Fact]
    public async Task Handle_AfterLocalMidnight_ReturnsZeroDailyAiUsage()
    {
        var user = CreateTestUser();
        for (var i = 0; i < 5; i++)
            user.IncrementAiMessageCount(Today.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(UserId, Arg.Any<CancellationToken>()).Returns(50);

        var result = await _handler.Handle(
            new GetSubscriptionStatusQuery(UserId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessagesUsed.Should().Be(0);
    }

    [Fact]
    public async Task Handle_LapsedSubscription_ReturnsReasonAndEndingDate()
    {
        var user = CreateTestUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly);
        user.CancelStripeSubscription(SubscriptionLapseReason.PaymentFailed);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(10);

        var result = await _handler.Handle(new GetSubscriptionStatusQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Plan.Should().Be("free");
        result.Value.LapseReason.Should().Be("payment_failed");
        result.Value.SubscriptionEndedAtUtc.Should().Be(user.SubscriptionEndedAtUtc);
    }
}
