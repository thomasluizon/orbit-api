using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using System.Linq.Expressions;
using System.Text.Json;

namespace Orbit.Application.Tests.Queries.Gamification;

public class GetStreakInfoQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<StreakFreeze> _streakFreezeRepo = Substitute.For<IGenericRepository<StreakFreeze>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUserStreakService _userStreakService = Substitute.For<IUserStreakService>();
    private readonly IFeatureFlagService _featureFlagService = Substitute.For<IFeatureFlagService>();
    private readonly IProductAnalytics _productAnalytics = Substitute.For<IProductAnalytics>();
    private readonly GetStreakInfoQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public GetStreakInfoQueryHandlerTests()
    {
        _featureFlagService.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());
        _handler = new GetStreakInfoQueryHandler(
            _userRepo,
            _streakFreezeRepo,
            _habitRepo,
            _userDateService,
            _userStreakService,
            _featureFlagService,
            _productAnalytics,
            NullLogger<GetStreakInfoQueryHandler>.Instance);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _userStreakService.EvaluateRepairAsync(
            UserId,
            Today,
            Today.AddDays(-1),
            Arg.Any<CancellationToken>())
            .Returns(StreakRepairEvaluation.Unavailable(Today.AddDays(-1)));
        _userStreakService.GetRepairableGapDatesAsync(
            UserId,
            Today,
            Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DateOnly>());
    }

    private static User CreateTestUser()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        typeof(User).GetProperty(nameof(User.Id))!.SetValue(user, UserId);
        return user;
    }

    private static User CreateFreeUser()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        typeof(User).GetProperty(nameof(User.Id))!.SetValue(user, UserId);
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        return user;
    }

    private void EnableFreeTierFlag()
    {
        _featureFlagService.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new[] { FeatureFlagKeys.GamificationFreeTier });
    }

    [Theory]
    [InlineData("America/Sao_Paulo", "2026-04-01", "active")]
    [InlineData("Pacific/Auckland", "2026-04-02", "active")]
    [InlineData("America/Sao_Paulo", null, "empty")]
    [InlineData("America/Sao_Paulo", null, "deleted")]
    [InlineData("America/Sao_Paulo", null, "completed")]
    public async Task Handle_FirstEligibleDateUsesEarliestActiveHabitInAccountTimeZone(
        string zoneId, string? expectedDate, string habitState)
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(CreateTestUser());
        _userDateService.GetUserTimeZoneAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(TimeZoneInfo.FindSystemTimeZoneById(zoneId));
        var habits = new List<Habit>();
        if (habitState != "empty")
        {
            var first = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, UserId,
                "First task", null, null, Today,
                CreatedAtUtc: new DateTime(2026, 4, 2, 1, 0, 0, DateTimeKind.Utc))).Value;
            if (habitState == "deleted") first.SoftDelete();
            if (habitState == "completed") first.Log(Today);
            habits.Add(first);
        }
        if (habitState == "active")
        {
            habits.Insert(0, Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, UserId,
                "Later task", null, null, Today,
                CreatedAtUtc: new DateTime(2026, 4, 3, 1, 0, 0, DateTimeKind.Utc))).Value);
            var deleted = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, UserId,
                "Deleted task", null, null, Today, CreatedAtUtc: DateTime.UnixEpoch)).Value;
            deleted.SoftDelete();
            habits.Add(deleted);
            var completed = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, UserId,
                "Completed task", null, null, Today, CreatedAtUtc: DateTime.UnixEpoch)).Value;
            completed.Log(Today);
            habits.Add(completed);
        }
        habits.Add(Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, Guid.NewGuid(),
            "Another account", null, null, Today, CreatedAtUtc: DateTime.UnixEpoch)).Value);
        _habitRepo.ProjectAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<DateTime>>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<DateTime>)call.Arg<Func<IQueryable<Habit>, IQueryable<DateTime>>>()(
                habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).AsQueryable()).ToArray());

        var result = await _handler.Handle(new GetStreakInfoQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FirstEligibleDate.Should().Be(expectedDate is null ? null : DateOnly.Parse(expectedDate));
        var json = JsonSerializer.SerializeToElement(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.TryGetProperty("firstEligibleDate", out var firstDate).Should().Be(expectedDate is not null);
        if (expectedDate is not null) firstDate.GetString().Should().Be(expectedDate);
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
        result.Value.IsRepairAvailable.Should().BeFalse();
        result.Value.RepairDate.Should().BeNull();
        result.Value.RepairsRemainingThisMonth.Should().Be(0);
        result.Value.LastFreezeCoveredDate.Should().BeNull();
        result.Value.FreezeBankRemaining.Should().BeNull();
        result.Value.RepairableGapDates.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_CalculatesStreakWithoutMutation_AndReturnsFreshValues()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _userStreakService.CalculateAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(11, 43, Today));

        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());

        var query = new GetStreakInfoQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentStreak.Should().Be(11);
        result.Value.LongestStreak.Should().Be(43);
        result.Value.LastActiveDate.Should().Be(Today);
        await _userStreakService.Received(1).CalculateAsync(
            UserId,
            Arg.Any<CancellationToken>());
        await _userStreakService.DidNotReceive().RecalculateAsync(
            Arg.Any<Guid>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var query = new GetStreakInfoQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.UserNotFound.Message);
        result.ErrorCode.Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_WithRecentFreezes_ReportsLatestCoveredDateAndRemainingBank()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var freezes = new List<StreakFreeze>
        {
            StreakFreeze.Create(UserId, Today.AddDays(-1), StreakFreezeOrigin.Automatic),
            StreakFreeze.Create(UserId, Today.AddDays(-3), StreakFreezeOrigin.Manual)
        };
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(freezes.AsReadOnly());

        var query = new GetStreakInfoQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FreezesUsedThisMonth.Should().Be(2);
        result.Value.FreezesAvailable.Should().Be(0);
        result.Value.RecentFreezeDates.Should().HaveCount(2);
        result.Value.IsFrozenToday.Should().BeFalse();
        result.Value.LastFreezeCoveredDate.Should().Be(Today.AddDays(-1));
        result.Value.FreezeBankRemaining.Should().Be(0);
        result.Value.LastFreezeCoveredOrigin.Should().Be("automatic");
    }

    [Fact]
    public async Task Handle_UnrecordedLatestFreeze_LeavesOriginUnknown()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        var freezes = new List<StreakFreeze>
        {
            StreakFreeze.Create(UserId, Today.AddDays(-1), StreakFreezeOrigin.Automatic),
            StreakFreeze.Create(UserId, Today.AddDays(-2), StreakFreezeOrigin.Automatic)
        };
        typeof(StreakFreeze).GetProperty(nameof(StreakFreeze.Origin))!
            .SetValue(freezes[0], null);
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>()).Returns(freezes.AsReadOnly());

        var result = await _handler.Handle(new GetStreakInfoQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.LastFreezeCoveredDate.Should().Be(Today.AddDays(-1));
        result.Value.LastFreezeCoveredOrigin.Should().BeNull();
    }

    [Fact]
    public async Task Handle_FrozenToday_ReportsCorrectly()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var freezes = new List<StreakFreeze>
        {
            StreakFreeze.Create(UserId, Today, StreakFreezeOrigin.Manual)
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
            StreakFreeze.Create(UserId, Today.AddDays(-1), StreakFreezeOrigin.Manual),
            StreakFreeze.Create(UserId, Today.AddDays(-5), StreakFreezeOrigin.Manual),
            StreakFreeze.Create(UserId, Today.AddDays(-10), StreakFreezeOrigin.Manual)
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

    [Fact]
    public async Task Handle_FreeUser_FlagOff_ReturnsPayGate()
    {
        var user = CreateFreeUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _handler.Handle(new GetStreakInfoQuery(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task Handle_FreeUser_FlagOn_ReturnsStreakInfo()
    {
        var user = CreateFreeUser();
        EnableFreeTierFlag();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());

        var result = await _handler.Handle(new GetStreakInfoQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_RepairAvailable_AppendsRepairFieldsAndCapturesOffer()
    {
        var user = CreateTestUser();
        user.SetStreakState(7, 7, Today.AddDays(-2));
        user.AwardStreakFreezeIfEligible();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());
        _userStreakService.EvaluateRepairAsync(
            UserId,
            Today,
            Today.AddDays(-1),
            Arg.Any<CancellationToken>())
            .Returns(StreakRepairEvaluation.Available(
                Today.AddDays(-1),
                new UserStreakState(7, 7, Today.AddDays(-1))));

        var result = await _handler.Handle(new GetStreakInfoQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsRepairAvailable.Should().BeTrue();
        result.Value.RepairDate.Should().Be(Today.AddDays(-1));
        result.Value.RepairsRemainingThisMonth.Should().Be(1);
        _productAnalytics.Received(1).CaptureUserEvent(
            UserId,
            "streak_repair_offered",
            user.Plan.ToString(),
            Arg.Is<IReadOnlyDictionary<string, object>>(properties =>
                (string)properties["missed_date"] == "2026-04-02"
                && (int)properties["remaining_bank"] == 1));
    }

    [Fact]
    public async Task Handle_AppendsRepairableGapDates()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());
        var dates = new[] { Today.AddDays(-8), Today.AddDays(-1) };
        _userStreakService.GetRepairableGapDatesAsync(
            UserId,
            Today,
            Arg.Any<CancellationToken>())
            .Returns(dates);

        var result = await _handler.Handle(new GetStreakInfoQuery(UserId), CancellationToken.None);

        result.Value.RepairableGapDates.Should().Equal(dates);
    }
}
