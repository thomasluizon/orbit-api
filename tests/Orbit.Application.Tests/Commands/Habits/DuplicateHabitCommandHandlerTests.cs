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
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly DuplicateHabitCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public DuplicateHabitCommandHandlerTests()
    {
        _handler = new DuplicateHabitCommandHandler(_habitRepo, _habitLogRepo, _payGate, _unitOfWork, Substitute.For<IUserDateService>(), _cache);

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
        result.Error.Should().Be(ErrorMessages.HabitNotFound.Message);
        result.ErrorCode.Should().Be(ErrorCodes.HabitNotFound);
    }

    [Fact]
    public async Task Handle_PayGateLimitReached_ReturnsPayGateFailure()
    {
        var original = Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value;
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
            UserId, "Complex Habit", FrequencyUnit.Day, 1,
            Description: "Daily check",
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
                h.FrequencyUnit == FrequencyUnit.Day &&
                h.FrequencyQuantity == 1 &&
                h.Description == "Daily check"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithChildren_DuplicatesChildHabits()
    {
        var parent = Habit.Create(new HabitCreateParams(
            UserId, "Morning Routine", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value;
        var child = Habit.Create(new HabitCreateParams(
            UserId, "Brush teeth", FrequencyUnit.Day, 1,
            ParentHabitId: parent.Id, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value;

        SetupAllHabitsForUser(new List<Habit> { parent, child });

        var command = new DuplicateHabitCommand(UserId, parent.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _habitRepo.Received(2).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        await _payGate.Received(1).CanCreateSubHabits(UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithChildren_SubHabitPayGated_ReturnsFailure()
    {
        var parent = Habit.Create(new HabitCreateParams(
            UserId, "Routine", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value;
        var child = Habit.Create(new HabitCreateParams(
            UserId, "Sub", FrequencyUnit.Day, 1,
            ParentHabitId: parent.Id, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value;

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
        var original = Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value;
        SetupAllHabitsForUser(new List<Habit> { original });

        var realToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var cacheKey = $"summary:{UserId}:{realToday:yyyy-MM-dd}:en";
        _cache.Set(cacheKey, "cached-summary");

        var command = new DuplicateHabitCommand(UserId, original.Id);

        await _handler.Handle(command, CancellationToken.None);

        _cache.TryGetValue(cacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_CompletedOneTimeTask_PreservesCompletionOnDuplicate()
    {
        var original = Habit.Create(new HabitCreateParams(
            UserId, "Buy hiking boots", null, null,
            DueDate: new DateOnly(2026, 6, 1))).Value;
        original.Log(new DateOnly(2026, 6, 2), "found them on sale").IsSuccess.Should().BeTrue();

        SetupAllHabitsForUser(new List<Habit> { original });
        SetupCompletionLogs(original.Logs);
        var added = CaptureAddedHabits();

        var result = await _handler.Handle(new DuplicateHabitCommand(UserId, original.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var copy = added.Single();
        copy.Id.Should().NotBe(original.Id);
        copy.IsCompleted.Should().BeTrue();
        copy.Logs.Should().ContainSingle(l =>
            l.Date == new DateOnly(2026, 6, 2) && l.Note == "found them on sale" && l.Value > 0);
    }

    [Fact]
    public async Task Handle_CompletedOneTimeChild_PreservesCompletionOnDuplicatedChild()
    {
        var parent = Habit.Create(new HabitCreateParams(
            UserId, "Plan trip", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value;
        var child = Habit.Create(new HabitCreateParams(
            UserId, "Book hotel", null, null,
            ParentHabitId: parent.Id, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value;
        child.Log(new DateOnly(2026, 6, 3)).IsSuccess.Should().BeTrue();

        SetupAllHabitsForUser(new List<Habit> { parent, child });
        SetupCompletionLogs(child.Logs);
        var added = CaptureAddedHabits();

        var result = await _handler.Handle(new DuplicateHabitCommand(UserId, parent.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var childCopy = added.Single(h => h.Title == "Book hotel");
        childCopy.IsCompleted.Should().BeTrue();
        childCopy.Logs.Should().ContainSingle(l => l.Date == new DateOnly(2026, 6, 3));
        var parentCopy = added.Single(h => h.Title == "Plan trip");
        parentCopy.IsCompleted.Should().BeFalse();
        parentCopy.Logs.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_UncompletedOneTimeTask_StaysUncompleted()
    {
        var original = Habit.Create(new HabitCreateParams(UserId, "Renew passport", null, null, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value;

        SetupAllHabitsForUser(new List<Habit> { original });
        var added = CaptureAddedHabits();

        var result = await _handler.Handle(new DuplicateHabitCommand(UserId, original.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        added.Single().IsCompleted.Should().BeFalse();
        added.Single().Logs.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_RecurringHabitWithLogs_DoesNotCopyLogs()
    {
        var original = Habit.Create(new HabitCreateParams(
            UserId, "Run", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 6, 1))).Value;
        original.Log(new DateOnly(2026, 6, 1)).IsSuccess.Should().BeTrue();

        SetupAllHabitsForUser(new List<Habit> { original });
        var added = CaptureAddedHabits();

        var result = await _handler.Handle(new DuplicateHabitCommand(UserId, original.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        added.Single().Logs.Should().BeEmpty();
        added.Single().IsCompleted.Should().BeFalse();
        await _habitLogRepo.DidNotReceive().FindAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>());
    }

    private void SetupAllHabitsForUser(List<Habit> habits)
    {
        _habitRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habits);
    }

    private void SetupCompletionLogs(IEnumerable<HabitLog> logs)
    {
        _habitLogRepo.FindAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(logs.ToList());
    }

    private List<Habit> CaptureAddedHabits()
    {
        var added = new List<Habit>();
        _habitRepo.AddAsync(Arg.Do<Habit>(added.Add), Arg.Any<CancellationToken>());
        return added;
    }
}
