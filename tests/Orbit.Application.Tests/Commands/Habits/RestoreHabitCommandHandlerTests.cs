using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Habits;

public class RestoreHabitCommandHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUserStreakService _userStreakService = Substitute.For<IUserStreakService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly RestoreHabitCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public RestoreHabitCommandHandlerTests()
    {
        _handler = new RestoreHabitCommandHandler(_habitRepo, _userStreakService, _unitOfWork, _userDateService, _cache);
        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Today);
        _userStreakService.RecalculateAsync(UserId, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(0, 0, null));
    }

    private static Habit CreateHabit(Guid? parentId = null)
    {
        return Habit.Create(new HabitCreateParams(
            UserId, "Habit", FrequencyUnit.Day, 1, DueDate: Today, ParentHabitId: parentId)).Value;
    }

    private void SetupUserHabits(params Habit[] habits)
    {
        _habitRepo.FindTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(habits.ToList());
    }

    [Fact]
    public async Task Handle_RestoresHabitAndCascadeDeletedSubtree()
    {
        var parent = CreateHabit();
        var child = CreateHabit(parentId: parent.Id);
        var deletedAt = new DateTime(2026, 3, 20, 10, 0, 0, DateTimeKind.Utc);
        parent.SoftDelete(deletedAt);
        child.SoftDelete(deletedAt);
        SetupUserHabits(parent, child);

        var result = await _handler.Handle(new RestoreHabitCommand(UserId, parent.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        parent.IsDeleted.Should().BeFalse();
        child.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_DoesNotRestoreChildDeletedInSeparateAction()
    {
        var parent = CreateHabit();
        var child = CreateHabit(parentId: parent.Id);
        child.SoftDelete(new DateTime(2026, 3, 18, 8, 0, 0, DateTimeKind.Utc));
        parent.SoftDelete(new DateTime(2026, 3, 20, 10, 0, 0, DateTimeKind.Utc));
        SetupUserHabits(parent, child);

        var result = await _handler.Handle(new RestoreHabitCommand(UserId, parent.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        parent.IsDeleted.Should().BeFalse();
        child.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_HabitNotDeleted_ReturnsFailure()
    {
        var habit = CreateHabit();
        SetupUserHabits(habit);

        var result = await _handler.Handle(new RestoreHabitCommand(UserId, habit.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Habit not found.");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WrongUser_ReturnsNotFound()
    {
        SetupUserHabits();

        var result = await _handler.Handle(new RestoreHabitCommand(UserId, Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Habit not found.");
    }
}
