using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Habits;

public class DuplicateHabitCommandHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly DuplicateHabitCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public DuplicateHabitCommandHandlerTests()
    {
        _handler = new DuplicateHabitCommandHandler(_habitRepo, _payGate, _unitOfWork, _cache);

        _payGate.CanCreateHabits(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _payGate.CanCreateSubHabits(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
    }

    [Fact]
    public async Task Handle_ValidHabit_DuplicatesSuccessfully()
    {
        var original = Habit.Create(new HabitCreateParams(
            UserId, "Exercise", FrequencyUnit.Day, 1,
            Description: "Morning workout",
            DueDate: new DateOnly(2026, 3, 20))).Value;

        SetupAllHabitsForUser(new List<Habit> { original });

        var command = new DuplicateHabitCommand(UserId, original.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBe(original.Id);
        // Original + duplicate = 2nd AddAsync call is the duplicate
        await _habitRepo.Received(1).AddAsync(
            Arg.Is<Habit>(h => h.Title == "Exercise" && h.Id != original.Id),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_HabitNotFound_ReturnsFailure()
    {
        SetupAllHabitsForUser(new List<Habit>());

        var command = new DuplicateHabitCommand(UserId, Guid.NewGuid());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.HabitNotFound);
        result.ErrorCode.Should().Be(ErrorCodes.HabitNotFound);
    }

    [Fact]
    public async Task Handle_PayGateLimitReached_ReturnsPayGateFailure()
    {
        var original = Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1)).Value;
        SetupAllHabitsForUser(new List<Habit> { original });

        _payGate.CanCreateHabits(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Habit limit reached"));

        var command = new DuplicateHabitCommand(UserId, original.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task Handle_DuplicatesAllProperties()
    {
        var original = Habit.Create(new HabitCreateParams(
            UserId, "Complex Habit", FrequencyUnit.Week, 1,
            Description: "Weekly check",
            Days: new List<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Friday },
            DueDate: new DateOnly(2026, 3, 20),
            DueTime: new TimeOnly(9, 0),
            DueEndTime: new TimeOnly(10, 0),
            IsGeneral: false,
            IsFlexible: false)).Value;

        SetupAllHabitsForUser(new List<Habit> { original });

        var command = new DuplicateHabitCommand(UserId, original.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _habitRepo.Received(1).AddAsync(
            Arg.Is<Habit>(h =>
                h.Title == "Complex Habit" &&
                h.FrequencyUnit == FrequencyUnit.Week &&
                h.FrequencyQuantity == 1 &&
                h.Description == "Weekly check"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithChildren_DuplicatesChildHabits()
    {
        var parent = Habit.Create(new HabitCreateParams(
            UserId, "Morning Routine", FrequencyUnit.Day, 1)).Value;
        var child = Habit.Create(new HabitCreateParams(
            UserId, "Brush teeth", FrequencyUnit.Day, 1,
            ParentHabitId: parent.Id)).Value;

        SetupAllHabitsForUser(new List<Habit> { parent, child });

        var command = new DuplicateHabitCommand(UserId, parent.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // Parent duplicate + child duplicate = 2 AddAsync calls
        await _habitRepo.Received(2).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        await _payGate.Received(1).CanCreateSubHabits(UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithChildren_SubHabitPayGated_ReturnsFailure()
    {
        var parent = Habit.Create(new HabitCreateParams(
            UserId, "Routine", FrequencyUnit.Day, 1)).Value;
        var child = Habit.Create(new HabitCreateParams(
            UserId, "Sub", FrequencyUnit.Day, 1,
            ParentHabitId: parent.Id)).Value;

        SetupAllHabitsForUser(new List<Habit> { parent, child });

        _payGate.CanCreateSubHabits(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Sub-habits are a Pro feature"));

        var command = new DuplicateHabitCommand(UserId, parent.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task Handle_InvalidatesSummaryCache()
    {
        var original = Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1)).Value;
        SetupAllHabitsForUser(new List<Habit> { original });

        var realToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var cacheKey = $"summary:{UserId}:{realToday:yyyy-MM-dd}:en";
        _cache.Set(cacheKey, "cached-summary");

        var command = new DuplicateHabitCommand(UserId, original.Id);

        await _handler.Handle(command, CancellationToken.None);

        _cache.TryGetValue(cacheKey, out _).Should().BeFalse();
    }

    private void SetupAllHabitsForUser(List<Habit> habits)
    {
        _habitRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habits);
    }
}
