using FluentAssertions;
using NSubstitute;
using Orbit.Application.Gamification.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Gamification;

public class GetStreakInfoQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<StreakFreeze> _streakFreezeRepo = Substitute.For<IGenericRepository<StreakFreeze>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly GetStreakInfoQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public GetStreakInfoQueryHandlerTests()
    {
        _handler = new GetStreakInfoQueryHandler(_userRepo, _streakFreezeRepo, _userDateService);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
    }

    private static User CreateTestUser()
    {
        return User.Create("Test User", "test@example.com").Value;
    }

    [Fact]
    public async Task Handle_UserFound_ReturnsStreakInfo()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());

        var query = new GetStreakInfoQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentStreak.Should().Be(0);
        result.Value.LongestStreak.Should().Be(0);
        result.Value.FreezesUsedThisMonth.Should().Be(0);
        result.Value.FreezesAvailable.Should().Be(0);
        result.Value.FreezesAvailableToUse.Should().Be(0);
        result.Value.MaxFreezesPerMonth.Should().Be(3);
        result.Value.StreakFreezesAccumulated.Should().Be(0);
        result.Value.MaxStreakFreezesAccumulated.Should().Be(3);
        result.Value.CanEarnMore.Should().BeTrue();
        result.Value.IsFrozenToday.Should().BeFalse();
        result.Value.RecentFreezeDates.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var query = new GetStreakInfoQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
        result.ErrorCode.Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_WithRecentFreezes_CalculatesCorrectly()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var freezes = new List<StreakFreeze>
        {
            StreakFreeze.Create(UserId, Today.AddDays(-1)),
            StreakFreeze.Create(UserId, Today.AddDays(-3))
        };
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(freezes.AsReadOnly());

        var query = new GetStreakInfoQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FreezesUsedThisMonth.Should().Be(2);
        // FreezesAvailable now reflects min(accumulated, monthly remaining). User hasn't
        // earned any freezes, so available is 0 despite monthly room.
        result.Value.FreezesAvailable.Should().Be(0);
        result.Value.RecentFreezeDates.Should().HaveCount(2);
        result.Value.IsFrozenToday.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_FrozenToday_ReportsCorrectly()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var freezes = new List<StreakFreeze>
        {
            StreakFreeze.Create(UserId, Today)
        };
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(freezes.AsReadOnly());

        var query = new GetStreakInfoQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsFrozenToday.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_AllFreezesUsed_ReturnsZeroAvailable()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var freezes = new List<StreakFreeze>
        {
            StreakFreeze.Create(UserId, Today.AddDays(-1)),
            StreakFreeze.Create(UserId, Today.AddDays(-5)),
            StreakFreeze.Create(UserId, Today.AddDays(-10))
        };
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(freezes.AsReadOnly());

        var query = new GetStreakInfoQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FreezesAvailable.Should().Be(0);
        result.Value.FreezesUsedThisMonth.Should().Be(3);
    }
}
