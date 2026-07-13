using FluentAssertions;
using NSubstitute;
using Orbit.Application.Gamification.Backfill;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Services;

public class XpAwardLogBackfillServiceTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<UserAchievement> _achievementRepo = Substitute.For<IGenericRepository<UserAchievement>>();
    private readonly IGenericRepository<XpAwardLog> _xpRepo = Substitute.For<IGenericRepository<XpAwardLog>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly XpAwardLogBackfillService _sut;
    private readonly List<XpAwardLog> _added = new();

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    private static readonly int[] ExpectedHabitXpAmounts = new[] { 11, 12, 13 };

    public XpAwardLogBackfillServiceTests()
    {
        _sut = new XpAwardLogBackfillService(_userRepo, _habitRepo, _goalRepo, _achievementRepo, _xpRepo, _unitOfWork);

        _xpRepo.AnyAsync(Arg.Any<Expression<Func<XpAwardLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _xpRepo.When(r => r.AddAsync(Arg.Any<XpAwardLog>(), Arg.Any<CancellationToken>()))
            .Do(ci => _added.Add(ci.Arg<XpAwardLog>()));

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>());
        _goalRepo.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Goal>());
        _achievementRepo.FindAsync(Arg.Any<Expression<Func<UserAchievement, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserAchievement>());
    }

    private static User CreateUserWithXp(int totalXp)
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.AddXp(totalXp);
        return user;
    }

    private static Habit CreateDailyHabitWithStreak(int days)
    {
        var startDate = Today.AddDays(-(days - 1));
        var habit = Habit.Create(new HabitCreateParams(UserId, "Daily", FrequencyUnit.Day, 1, DueDate: startDate)).Value;
        typeof(Habit).GetProperty("CreatedAtUtc")!.SetValue(habit, startDate.ToDateTime(TimeOnly.MinValue));
        for (var i = 0; i < days; i++)
            habit.Log(startDate.AddDays(i));
        return habit;
    }

    private void ArrangeHabit(Habit habit)
    {
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit });
    }

    [Fact]
    public async Task BackfillUser_ReplaysHabitXpWithRecomputedPerDateStreak()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(CreateUserWithXp(36));
        ArrangeHabit(CreateDailyHabitWithStreak(3));

        var processed = await _sut.BackfillUserAsync(UserId);

        processed.Should().BeTrue();
        _added.Should().HaveCount(3);
        _added.Should().OnlyContain(r => r.Source == XpAwardSource.HabitLog);
        _added.Select(r => r.Amount).Should().BeEquivalentTo(ExpectedHabitXpAmounts);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BackfillUser_PinsCumulativeToTotalXpWithSingleReconciliationRow()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(CreateUserWithXp(50));
        ArrangeHabit(CreateDailyHabitWithStreak(3));

        await _sut.BackfillUserAsync(UserId);

        _added.Should().HaveCount(4);
        var reconciliation = _added.Single(r => r.Source == XpAwardSource.Reconciliation);
        reconciliation.Amount.Should().Be(14);
        _added.Sum(r => r.Amount).Should().Be(50);
    }

    [Fact]
    public async Task BackfillUser_NoDrift_WritesNoReconciliationRow()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(CreateUserWithXp(36));
        ArrangeHabit(CreateDailyHabitWithStreak(3));

        await _sut.BackfillUserAsync(UserId);

        _added.Should().NotContain(r => r.Source == XpAwardSource.Reconciliation);
    }

    [Fact]
    public async Task BackfillUser_AlreadyHasRows_NoOps()
    {
        _xpRepo.AnyAsync(Arg.Any<Expression<Func<XpAwardLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var processed = await _sut.BackfillUserAsync(UserId);

        processed.Should().BeFalse();
        _added.Should().BeEmpty();
        await _xpRepo.DidNotReceive().AddAsync(Arg.Any<XpAwardLog>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
