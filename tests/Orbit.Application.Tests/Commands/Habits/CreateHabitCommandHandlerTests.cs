using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Habits;

public class CreateHabitCommandHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<Tag> _tagRepo = Substitute.For<IGenericRepository<Tag>>();
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IGamificationService _gamificationService = Substitute.For<IGamificationService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly CreateHabitCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public CreateHabitCommandHandlerTests()
    {
        var repos = new CreateHabitRepositories(_habitRepo, _tagRepo, _goalRepo);
        _handler = new CreateHabitCommandHandler(
            repos, _userDateService, _payGate, _gamificationService, _unitOfWork, _cache,
            Substitute.For<ILogger<CreateHabitCommandHandler>>());

        _payGate.CanCreateHabits(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _payGate.CanCreateSubHabits(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Today);
    }

    [Fact]
    public async Task Handle_ValidCommand_CreatesHabitAndSaves()
    {
        var command = new CreateHabitCommand(
            UserId, "Read 30 minutes", "Daily reading", FrequencyUnit.Day, 1);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeEmpty();
        await _habitRepo.Received(1).AddAsync(
            Arg.Is<Habit>(h => h.Title == "Read 30 minutes" && h.UserId == UserId),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_FirstRootHabit_AssignsPositionZero()
    {
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>());

        var command = new CreateHabitCommand(UserId, "First", null, FrequencyUnit.Day, 1);
        await _handler.Handle(command, CancellationToken.None);

        await _habitRepo.Received().AddAsync(
            Arg.Is<Habit>(h => h.Position == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingRootHabits_AssignsMaxPositionPlusOne()
    {
        var existing1 = Habit.Create(new HabitCreateParams(UserId, "A", FrequencyUnit.Day, 1, DueDate: Today, Position: 0)).Value;
        var existing2 = Habit.Create(new HabitCreateParams(UserId, "B", FrequencyUnit.Day, 1, DueDate: Today, Position: 5)).Value;
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { existing1, existing2 });

        var command = new CreateHabitCommand(UserId, "New", null, FrequencyUnit.Day, 1);
        await _handler.Handle(command, CancellationToken.None);

        await _habitRepo.Received().AddAsync(
            Arg.Is<Habit>(h => h.Title == "New" && h.Position == 6),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithSubHabits_CreatesParentAndChildren()
    {
        var command = new CreateHabitCommand(
            UserId, "Morning Routine", null, FrequencyUnit.Day, 1,
            SubHabits: new List<string> { "Brush teeth", "Stretch" });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // Parent + 2 children = 3 AddAsync calls
        await _habitRepo.Received(3).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        await _payGate.Received(1).CanCreateSubHabits(UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithTagIds_AssignsTagsToHabit()
    {
        var tagId = Guid.NewGuid();
        var tag = Tag.Create(UserId, "Health", "#00ff00").Value;

        _tagRepo.FindTrackedAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Tag> { tag });

        var command = new CreateHabitCommand(
            UserId, "Exercise", null, FrequencyUnit.Day, 1,
            TagIds: new List<Guid> { tagId });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _tagRepo.Received(1).FindTrackedAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PayGateLimitReached_ReturnsPayGateFailure()
    {
        _payGate.CanCreateHabits(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Habit limit reached"));

        var command = new CreateHabitCommand(UserId, "New habit", null, FrequencyUnit.Day, 1);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SubHabitsPayGated_ReturnsPayGateFailure()
    {
        _payGate.CanCreateSubHabits(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Sub-habits are a Pro feature"));

        var command = new CreateHabitCommand(
            UserId, "Morning Routine", null, FrequencyUnit.Day, 1,
            SubHabits: new List<string> { "Brush teeth" });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task Handle_InvalidTitle_ReturnsFailure()
    {
        var command = new CreateHabitCommand(UserId, "", null, FrequencyUnit.Day, 1);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Title");
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvalidatesSummaryCache()
    {
        // CacheInvalidationHelper uses DateOnly.FromDateTime(DateTime.UtcNow) internally,
        // so the test cache key must use the real UTC date (not the mocked Today).
        var realToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var cacheKey = $"summary:{UserId}:{realToday:yyyy-MM-dd}:en";
        _cache.Set(cacheKey, "cached-summary");

        var command = new CreateHabitCommand(UserId, "Test habit", null, FrequencyUnit.Day, 1);

        await _handler.Handle(command, CancellationToken.None);

        _cache.TryGetValue(cacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_WithGoalIds_LinksGoalsToHabit()
    {
        var goalId = Guid.NewGuid();
        var goal = Goal.Create(UserId, "Fitness Goal", 10, "workouts").Value;

        _goalRepo.FindTrackedAsync(Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal });

        var command = new CreateHabitCommand(
            UserId, "Exercise", null, FrequencyUnit.Day, 1,
            GoalIds: new List<Guid> { goalId });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _goalRepo.Received(1).FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_BadHabitCreation_Success()
    {
        var command = new CreateHabitCommand(
            UserId, "Smoking", "Quit smoking", FrequencyUnit.Day, 1, IsBadHabit: true);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _habitRepo.Received(1).AddAsync(
            Arg.Is<Habit>(h => h.IsBadHabit),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_OneTimeTask_NoFrequency()
    {
        var command = new CreateHabitCommand(
            UserId, "One-time task", null, null, null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _habitRepo.Received(1).AddAsync(
            Arg.Is<Habit>(h => h.FrequencyUnit == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_FlexibleHabit_Success()
    {
        var command = new CreateHabitCommand(
            UserId, "Flexible Workout", null, FrequencyUnit.Week, 3,
            Options: new HabitCommandOptions(IsFlexible: true));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _habitRepo.Received(1).AddAsync(
            Arg.Is<Habit>(h => h.IsFlexible),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithDueDate_UsesProvidedDate()
    {
        var dueDate = new DateOnly(2026, 6, 15);
        var command = new CreateHabitCommand(
            UserId, "Future Habit", null, FrequencyUnit.Day, 1, DueDate: dueDate);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _habitRepo.Received(1).AddAsync(
            Arg.Is<Habit>(h => h.DueDate == dueDate),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithoutDueDate_UsesUserToday()
    {
        var command = new CreateHabitCommand(
            UserId, "Today Habit", null, FrequencyUnit.Day, 1);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _habitRepo.Received(1).AddAsync(
            Arg.Is<Habit>(h => h.DueDate == Today),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithOptions_PassesDaysAndTimes()
    {
        var options = new HabitCommandOptions(
            Days: new[] { DayOfWeek.Monday, DayOfWeek.Wednesday },
            DueTime: new TimeOnly(9, 0),
            DueEndTime: new TimeOnly(10, 0));

        var command = new CreateHabitCommand(
            UserId, "Scheduled Habit", null, FrequencyUnit.Day, 1,
            Options: options);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _habitRepo.Received(1).AddAsync(
            Arg.Is<Habit>(h => h.DueTime == new TimeOnly(9, 0)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithDays_SetsDueDateToNextMatchingDay()
    {
        // Today is Friday 2026-03-20; Days = Saturday, Sunday
        // DueDate should advance to Saturday 2026-03-21
        var options = new HabitCommandOptions(
            Days: new[] { DayOfWeek.Saturday, DayOfWeek.Sunday });

        var command = new CreateHabitCommand(
            UserId, "Weekend Habit", null, FrequencyUnit.Week, 1,
            Options: options);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _habitRepo.Received(1).AddAsync(
            Arg.Is<Habit>(h => h.DueDate == new DateOnly(2026, 3, 21)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithDays_IncludingToday_KeepsTodayAsDueDate()
    {
        // Today is Friday 2026-03-20; Days includes Friday
        // DueDate should stay as today
        var options = new HabitCommandOptions(
            Days: new[] { DayOfWeek.Friday, DayOfWeek.Saturday });

        var command = new CreateHabitCommand(
            UserId, "Friday Habit", null, FrequencyUnit.Week, 1,
            Options: options);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _habitRepo.Received(1).AddAsync(
            Arg.Is<Habit>(h => h.DueDate == Today),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SubHabitWithInvalidTitle_ReturnsFailure()
    {
        var command = new CreateHabitCommand(
            UserId, "Parent Habit", null, FrequencyUnit.Day, 1,
            SubHabits: new List<string> { "" });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_GamificationFailure_DoesNotBreakCreate()
    {
        _gamificationService.ProcessHabitCreated(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Gamification down"));

        var command = new CreateHabitCommand(UserId, "Test habit", null, FrequencyUnit.Day, 1);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_IsGeneral_CreatesGeneralHabit()
    {
        var command = new CreateHabitCommand(
            UserId, "General Habit", null, null, null, IsGeneral: true);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _habitRepo.Received(1).AddAsync(
            Arg.Is<Habit>(h => h.IsGeneral),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoTagsOrGoals_DoesNotQueryRepos()
    {
        var command = new CreateHabitCommand(
            UserId, "Simple habit", null, FrequencyUnit.Day, 1);

        await _handler.Handle(command, CancellationToken.None);

        await _tagRepo.DidNotReceive().FindTrackedAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>());
        await _goalRepo.DidNotReceive().FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<CancellationToken>());
    }
}
