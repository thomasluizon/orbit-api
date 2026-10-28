using FluentAssertions;
using System.Text.Json;
using NSubstitute;
using Orbit.Application.Subscriptions.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Application.Common;

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
    public async Task Handle_AppStore_EmitsNullableLegacySourceAndOptionalProvider()
    {
        var user = CreateTestUser();
        user.ApplyAppStoreSubscription("original", "Sandbox", DateTime.UtcNow.AddDays(30), SubscriptionInterval.Monthly, DateTime.UtcNow, true, null);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        var result = await _handler.Handle(new(UserId), default);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value, JsonSerializerOptions.Web));
        json.RootElement.GetProperty("source").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("billingProvider").GetString().Should().Be("app_store");
    }

    [Fact]
    public async Task Handle_UserFound_ReturnsStatus()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(250);

        var query = new GetSubscriptionStatusQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Plan.Should().Be("pro");
        result.Value.HasProAccess.Should().BeTrue();
        result.Value.IsTrialActive.Should().BeTrue();
        result.Value.AiMessagesUsed.Should().Be(0);
        result.Value.AiMessagesLimit.Should().Be(250);
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
        result.Error.Should().Be(ErrorMessages.UserNotFound.Message);
        result.ErrorCode.Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_PlaySubscription_ReturnsPlaySource()
    {
        var user = CreateTestUser();
        user.SetPlaySubscription("tok_123", DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(250);

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
        _payGate.GetAiMessageLimit(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(250);

        var query = new GetSubscriptionStatusQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsTrialActive.Should().BeTrue();
        result.Value.HasProAccess.Should().BeTrue();
        result.Value.Plan.Should().Be("pro");
    }

    [Theory]
    [InlineData(false, 0, "day", 5)]
    [InlineData(true, 5, "week", 250)]
    public async Task Handle_AfterLocalMidnight_UsesPlanPeriod(bool pro, int expectedUsed, string period, int limit)
    {
        var user = CreateTestUser();
        if (!pro) user.StartTrial(DateTime.UtcNow.AddDays(-1));
        for (var i = 0; i < 5; i++)
            user.IncrementAiMessageCount(Today.AddDays(-1));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(user.Id, Arg.Any<CancellationToken>()).Returns(limit);

        var result = await _handler.Handle(
            new GetSubscriptionStatusQuery(UserId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessagesUsed.Should().Be(expectedUsed);
        result.Value.AiMessagesLimit.Should().Be(limit);
        result.Value.AiMessagesPeriod.Should().Be(period);
        result.Value.AiMessagesResetsAt.Should().Be(new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc));
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
