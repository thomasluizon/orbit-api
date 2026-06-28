using FluentAssertions;
using NSubstitute;
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

    private static XpAwardLog Row(int amount, DateOnly date) =>
        XpAwardLog.Create(UserId, amount, XpAwardSource.HabitLog, null, date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

    [Fact]
    public async Task Handle_ProUser_ReturnsCumulativeCurveWithPreRangeBaseline()
    {
        var user = CreateProUser();
        user.AddXp(161);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var rows = new List<XpAwardLog>
        {
            Row(50, Today.AddDays(-10)),
            Row(11, Today.AddDays(-1)),
            Row(100, Today),
        };
        _xpRepo.FindAsync(Arg.Any<Expression<Func<XpAwardLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(rows);

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
    public async Task Handle_FreeUser_ReturnsPayGateFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(CreateFreeUser());

        var query = new GetXpHistoryQuery(UserId, Today.AddDays(-1), Today);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Orbit.Domain.Common.Result.PayGateErrorCode);
    }
}
