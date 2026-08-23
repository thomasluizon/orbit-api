using System.Text.Json;
using FluentAssertions;
using FluentValidation.TestHelper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Goals.Commands;
using Orbit.Application.Goals.Services;
using Orbit.Application.Goals.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Goals;

public class CreateGoalCommandHandlerTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IGamificationService _gamificationService = Substitute.For<IGamificationService>();
    private readonly IGoalCompletionService _goalCompletionService = Substitute.For<IGoalCompletionService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly CreateGoalCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 6, 13);

    public CreateGoalCommandHandlerTests()
    {
        _handler = new CreateGoalCommandHandler(
            _goalRepo, _habitRepo, _userDateService, _gamificationService,
            _goalCompletionService, _unitOfWork, _cache,
            Substitute.For<ILogger<CreateGoalCommandHandler>>());

        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Today);
    }

    [Fact]
    public async Task Handle_ValidCommand_CreatesGoalAndReturnsId()
    {
        var command = new CreateGoalCommand(UserId, "Run a marathon", "Train for 26.2 miles", 42.2m, "km", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeEmpty();
        await _goalRepo.Received(1).AddAsync(
            Arg.Is<Goal>(g => g.Title == "Run a marathon" && g.UserId == UserId),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithDeadline_CreatesGoalWithDeadline()
    {
        var deadline = new DateOnly(2026, 12, 31);
        var command = new CreateGoalCommand(UserId, "Learn piano", null, 100, "hours", deadline);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _goalRepo.Received(1).AddAsync(
            Arg.Is<Goal>(g => g.Title == "Learn piano" && g.Deadline == deadline),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EmptyTitle_ReturnsFailure()
    {
        var command = new CreateGoalCommand(UserId, "", null, 10, "units", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Title");
        await _goalRepo.DidNotReceive().AddAsync(Arg.Any<Goal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ZeroTargetValue_ReturnsFailure()
    {
        var command = new CreateGoalCommand(UserId, "Goal", null, 0, "units", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Target value");
        await _goalRepo.DidNotReceive().AddAsync(Arg.Any<Goal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EmptyUnit_ReturnsFailure()
    {
        var command = new CreateGoalCommand(UserId, "Goal", null, 10, "", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Unit");
        await _goalRepo.DidNotReceive().AddAsync(Arg.Any<Goal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CallsGamificationProcessGoalCreated()
    {
        var command = new CreateGoalCommand(UserId, "Goal", null, 10, "units", null);

        await _handler.Handle(command, CancellationToken.None);

        await _gamificationService.Received(1).ProcessGoalCreated(UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_GamificationThrows_StillReturnsSuccess()
    {
        _gamificationService.ProcessGoalCreated(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("gamification error"));

        var command = new CreateGoalCommand(UserId, "Goal", null, 10, "units", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_NegativeTargetValue_ReturnsFailure()
    {
        var command = new CreateGoalCommand(UserId, "Goal", null, -5, "units", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Target value");
    }

    [Fact]
    public async Task Handle_DeadlineInPast_ReturnsFailureAndDoesNotCreate()
    {
        var command = new CreateGoalCommand(UserId, "Goal", null, 10, "units", Today.AddDays(-1));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.DeadlineInPast);
        await _goalRepo.DidNotReceive().AddAsync(Arg.Any<Goal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DeadlineToday_CreatesGoal()
    {
        var command = new CreateGoalCommand(UserId, "Goal", null, 10, "units", Today);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _goalRepo.Received(1).AddAsync(
            Arg.Is<Goal>(g => g.Deadline == Today),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateGoal_WithoutHabitIds_CreatesGoalWithNoLinks()
    {
        var command = new CreateGoalCommand(UserId, "Goal", null, 10, "units", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _goalRepo.Received(1).AddAsync(
            Arg.Is<Goal>(goal => goal.Habits.Count == 0),
            Arg.Any<CancellationToken>());
        await _habitRepo.DidNotReceiveWithAnyArgs().FindTrackedAsync(default!, default);
    }

    [Fact]
    public async Task CreateGoal_WithHabitIds_LinksAllOfThem()
    {
        var first = CreateHabit("First");
        var second = CreateHabit("Second");
        _habitRepo.FindTrackedAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns([first, second]);
        var command = new CreateGoalCommand(UserId, "Goal", null, 10, "units", null, HabitIds: [first.Id, second.Id]);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _goalRepo.Received(1).AddAsync(
            Arg.Is<Goal>(goal => goal.Habits.Count == 2 && goal.Habits.Contains(first) && goal.Habits.Contains(second)),
            Arg.Any<CancellationToken>());
        await _goalCompletionService.Received(1).SyncDerivedGoalsAsync(
            UserId, Arg.Any<IReadOnlyCollection<Guid>>(), Today, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateGoal_WithForeignHabitId_FailsAndCreatesNoGoal()
    {
        _habitRepo.FindTrackedAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns([]);
        var command = new CreateGoalCommand(UserId, "Goal", null, 10, "units", null, HabitIds: [Guid.NewGuid()]);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.HabitNotFound);
        await _goalRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task CreateGoal_WithTooManyHabitIds_FailsAndCreatesNoGoal()
    {
        var habitIds = Enumerable.Range(0, AppConstants.MaxHabitsPerGoal + 1).Select(_ => Guid.NewGuid()).ToList();
        var command = new CreateGoalCommand(UserId, "Goal", null, 10, "units", null, HabitIds: habitIds);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MaxHabitsPerGoal);
        await _goalRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task CreateGoal_WithEmptyHabitIdList_BehavesAsAbsent()
    {
        var command = new CreateGoalCommand(UserId, "Goal", null, 10, "units", null, HabitIds: []);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _goalRepo.Received(1).AddAsync(
            Arg.Is<Goal>(goal => goal.Habits.Count == 0),
            Arg.Any<CancellationToken>());
        await _habitRepo.DidNotReceiveWithAnyArgs().FindTrackedAsync(default!, default);
    }

    [Fact]
    public async Task CreateGoal_StreakGoalWithHabit_SyncsProgressOnLink()
    {
        var habit = CreateHabit("Daily habit");
        typeof(Habit)
            .GetProperty(nameof(Habit.CreatedAtUtc))!
            .SetValue(habit, Today.AddDays(-2).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        habit.Log(Today.AddDays(-2), advanceDueDate: false);
        habit.Log(Today.AddDays(-1), advanceDueDate: false);
        habit.Log(Today, advanceDueDate: false);
        _habitRepo.FindTrackedAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns([habit]);
        Goal? createdGoal = null;
        _goalRepo.AddAsync(Arg.Do<Goal>(goal => createdGoal = goal), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var command = new CreateGoalCommand(UserId, "Streak", null, 7, "days", null, Type: GoalType.Streak, HabitIds: [habit.Id]);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        createdGoal.Should().NotBeNull();
        await _goalCompletionService.Received(1).SyncDerivedGoalsAsync(
            UserId,
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { createdGoal!.Id })),
            Today,
            false,
            Arg.Any<CancellationToken>());
    }

    private static Habit CreateHabit(string title) =>
        Habit.Create(new HabitCreateParams(UserId, title, FrequencyUnit.Day, 1, DueDate: Today)).Value;

    [Fact]
    public void Validate_HabitIdsOverLimit_HasError()
    {
        var habitIds = Enumerable.Range(0, AppConstants.MaxHabitsPerGoal + 1).Select(_ => Guid.NewGuid()).ToList();
        var command = new CreateGoalCommand(UserId, "Goal", null, 10, "units", null, HabitIds: habitIds);

        var result = new CreateGoalCommandValidator().TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.HabitIds);
    }
}

public class CreateGoalToolHabitLinkTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public void ParameterSchema_ExposesOptionalHabitIds()
    {
        var tool = new CreateGoalTool(_goalRepo, _unitOfWork, _habitRepo);

        JsonSerializer.Serialize(tool.GetParameterSchema()).Should().Contain("habit_ids");
    }

    [Fact]
    public async Task CreateGoalTool_WithHabitIds_LinksThem()
    {
        var habit = CreateHabit();
        _habitRepo.FindTrackedAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns([habit]);
        var tool = new CreateGoalTool(_goalRepo, _unitOfWork, _habitRepo);
        using var document = JsonDocument.Parse($$$"""{"title":"Read daily","habit_ids":["{{{habit.Id}}}"]}""");

        var result = await tool.ExecuteAsync(document.RootElement, UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        await _goalRepo.Received(1).AddAsync(
            Arg.Is<Goal>(goal => goal.Habits.Count == 1 && goal.Habits.Contains(habit)),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateGoalTool_WithForeignHabitId_FailsWithoutCreatingGoal()
    {
        _habitRepo.FindTrackedAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var tool = new CreateGoalTool(_goalRepo, _unitOfWork, _habitRepo);
        using var document = JsonDocument.Parse($$$"""{"title":"Read daily","habit_ids":["{{{Guid.NewGuid()}}}"]}""");

        var result = await tool.ExecuteAsync(document.RootElement, UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(ErrorMessages.HabitNotFound.Message);
        await _goalRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task CreateGoalTool_WithNonArrayHabitIds_ReturnsValidationFailure()
    {
        var tool = new CreateGoalTool(_goalRepo, _unitOfWork, _habitRepo);
        using var document = JsonDocument.Parse("""{"title":"Read daily","habit_ids":"not-an-array"}""");

        var result = await tool.ExecuteAsync(document.RootElement, UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("habit_ids must be an array.");
        await _goalRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("\"not-a-guid\"")]
    public async Task CreateGoalTool_WithInvalidHabitIdItem_ReturnsValidationFailure(string item)
    {
        var tool = new CreateGoalTool(_goalRepo, _unitOfWork, _habitRepo);
        using var document = JsonDocument.Parse($$$"""{"title":"Read daily","habit_ids":[{{{item}}}]}""");

        var result = await tool.ExecuteAsync(document.RootElement, UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("habit_ids must contain only valid GUID strings.");
        await _goalRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Fact]
    public async Task CreateGoalTool_WithEmptyHabitIds_CreatesWithoutLoadingHabits()
    {
        var tool = new CreateGoalTool(_goalRepo, _unitOfWork, _habitRepo);
        using var document = JsonDocument.Parse("""{"title":"Read daily","habit_ids":[]}""");

        var result = await tool.ExecuteAsync(document.RootElement, UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        await _habitRepo.DidNotReceiveWithAnyArgs().FindTrackedAsync(default!, default);
        await _goalRepo.Received(1).AddAsync(Arg.Any<Goal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateGoalTool_TwoArgumentConstruction_WithHabitIds_ReturnsFailure()
    {
        var tool = new CreateGoalTool(_goalRepo, _unitOfWork);
        using var document = JsonDocument.Parse($$$"""{"title":"Read daily","habit_ids":["{{{Guid.NewGuid()}}}"]}""");

        var result = await tool.ExecuteAsync(document.RootElement, UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("habit_ids is unavailable for this tool instance.");
        await _goalRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    private static Habit CreateHabit() =>
        Habit.Create(new HabitCreateParams(UserId, "Read", FrequencyUnit.Day, 1, DueDate: new DateOnly(2026, 8, 6))).Value;
}
