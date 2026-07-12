using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Gamification;

public class GetXpHistoryQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<XpAwardLog> _xpRepo = Substitute.For<IGenericRepository<XpAwardLog>>();
    private readonly IFeatureFlagService _featureFlags = Substitute.For<IFeatureFlagService>();
    private readonly GetXpHistoryQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public GetXpHistoryQueryHandlerTests()
    {
        _handler = new GetXpHistoryQueryHandler(_userRepo, _xpRepo, _featureFlags);
        _featureFlags.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<string>());
    }

    private static User CreateProUser()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetStripeSubscription("sub", DateTime.UtcNow.AddYears(1));
        return user;
    }

    private static User CreateFreeUser()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        return user;
    }

    private static XpAwardLog RowAt(int amount, DateTime awardedAtUtc) =>
        XpAwardLog.Create(UserId, amount, XpAwardSource.HabitLog, null, awardedAtUtc);

    private static XpAwardLog Row(int amount, DateOnly date) =>
        RowAt(amount, date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

    private void ArrangeBaseline(int baseline) =>
        _xpRepo.SumAsync(
            Arg.Any<Expression<Func<XpAwardLog, bool>>>(),
            Arg.Any<Expression<Func<XpAwardLog, int>>>(),
            Arg.Any<CancellationToken>())
            .Returns(baseline);

    private void ArrangeInRange(params XpAwardLog[] rows) =>
        _xpRepo.FindAsync(
            Arg.Any<Expression<Func<XpAwardLog, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(rows.ToList().AsReadOnly());

    [Fact]
    public async Task Handle_ProUser_CarriesPreRangeBaselineForwardAcrossDailyBuckets()
    {
        var user = CreateProUser();
        user.AddXp(161);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        ArrangeBaseline(50);
        ArrangeInRange(Row(11, Today.AddDays(-1)), Row(100, Today));

        var query = new GetXpHistoryQuery(UserId, Today.AddDays(-1), Today);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalXp.Should().Be(161);
        result.Value.Points.Should().HaveCount(2);
        result.Value.Points[0].Date.Should().Be(Today.AddDays(-1));
        result.Value.Points[0].DailyXp.Should().Be(11);
        result.Value.Points[0].CumulativeXp.Should().Be(61);
        result.Value.Points[1].DailyXp.Should().Be(100);
        result.Value.Points[1].CumulativeXp.Should().Be(161);
    }

    [Fact]
    public async Task Handle_NoRows_ReturnsFlatBaselineCurveAcrossEveryDay()
    {
        var user = CreateProUser();
        user.AddXp(40);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        ArrangeBaseline(40);
        ArrangeInRange();

        var query = new GetXpHistoryQuery(UserId, Today.AddDays(-2), Today);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Points.Should().HaveCount(3);
        result.Value.Points.Should().OnlyContain(p => p.DailyXp == 0 && p.CumulativeXp == 40);
    }

    [Fact]
    public async Task Handle_PushesBaselineAndRangeBoundariesIntoRepositoryPredicates()
    {
        var user = CreateProUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        Expression<Func<XpAwardLog, bool>>? baselinePredicate = null;
        Expression<Func<XpAwardLog, bool>>? rangePredicate = null;
        _xpRepo.SumAsync(
            Arg.Do<Expression<Func<XpAwardLog, bool>>>(p => baselinePredicate = p),
            Arg.Any<Expression<Func<XpAwardLog, int>>>(),
            Arg.Any<CancellationToken>())
            .Returns(0);
        _xpRepo.FindAsync(
            Arg.Do<Expression<Func<XpAwardLog, bool>>>(p => rangePredicate = p),
            Arg.Any<CancellationToken>())
            .Returns(new List<XpAwardLog>().AsReadOnly());

        var from = Today.AddDays(-3);
        var to = Today;
        await _handler.Handle(new GetXpHistoryQuery(UserId, from, to), CancellationToken.None);

        var fromMidnight = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var toNextMidnight = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var isBaseline = baselinePredicate!.Compile();
        isBaseline(RowAt(1, fromMidnight.AddTicks(-1))).Should().BeTrue();
        isBaseline(RowAt(1, fromMidnight)).Should().BeFalse();
        isBaseline(XpAwardLog.Create(Guid.NewGuid(), 1, XpAwardSource.HabitLog, null, fromMidnight.AddTicks(-1)))
            .Should().BeFalse();

        var isInRange = rangePredicate!.Compile();
        isInRange(RowAt(1, fromMidnight)).Should().BeTrue();
        isInRange(RowAt(1, toNextMidnight.AddTicks(-1))).Should().BeTrue();
        isInRange(RowAt(1, fromMidnight.AddTicks(-1))).Should().BeFalse();
        isInRange(RowAt(1, toNextMidnight)).Should().BeFalse();
        isInRange(XpAwardLog.Create(Guid.NewGuid(), 1, XpAwardSource.HabitLog, null, fromMidnight))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Handle_FreeUserWithGamificationFreeTierFlag_Unlocks()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(CreateFreeUser());
        _featureFlags.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { FeatureFlagKeys.GamificationFreeTier });
        ArrangeBaseline(0);
        ArrangeInRange(Row(5, Today));

        var query = new GetXpHistoryQuery(UserId, Today, Today);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Points.Should().ContainSingle();
        result.Value.Points[0].DailyXp.Should().Be(5);
    }

    [Fact]
    public async Task Handle_FreeUser_ReturnsPayGateFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(CreateFreeUser());

        var query = new GetXpHistoryQuery(UserId, Today.AddDays(-1), Today);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Orbit.Domain.Common.Result.PayGateErrorCode);
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var query = new GetXpHistoryQuery(UserId, Today.AddDays(-1), Today);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }
}
