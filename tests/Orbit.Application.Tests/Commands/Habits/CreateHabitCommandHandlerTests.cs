using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
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
    private readonly IGoalCompletionService _goalCompletionService = Substitute.For<IGoalCompletionService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly CreateHabitCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public CreateHabitCommandHandlerTests()
    {
        var repos = new CreateHabitRepositories(_habitRepo, _tagRepo, _goalRepo);
        _handler = new CreateHabitCommandHandler(
            repos, _userDateService, _payGate, _gamificationService, _goalCompletionService, _unitOfWork, _cache,
            Substitute.For<ILogger<CreateHabitCommandHandler>>());

        _payGate.CanCreateHabits(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _payGate.CanUseSlipAlerts(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Today);
        _unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<Result<Guid>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var operation = call.ArgAt<Func<CancellationToken, Task<Result<Guid>>>>(0);
                return operation(call.ArgAt<CancellationToken>(1));
            });
    }

    [Fact]
    public async Task Handle_ExplicitStartAtCalendarBoundary_ReturnsDomainFailure()
    {
        var command = new CreateHabitCommand(UserId, "Read", null, FrequencyUnit.Day, 1,
            StartDate: DateOnly.MaxValue, Options: new HabitCommandOptions(Days: [DayOfWeek.Monday]));
        (await _handler.Handle(command, CancellationToken.None)).IsFailure.Should().BeTrue();
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExplicitStart_PreservesSelectionBeforeWeekdayAlignment()
    {
        var selected = new DateOnly(2026, 10, 26);
        var command = new CreateHabitCommand(UserId, "Read", null, FrequencyUnit.Day, 1,
            StartDate: selected, Options: new HabitCommandOptions(Days: [DayOfWeek.Wednesday]));
        (await _handler.Handle(command, CancellationToken.None)).IsSuccess.Should().BeTrue();
        await _habitRepo.Received(1).AddAsync(Arg.Is<Habit>(habit => habit.StartDate == selected
            && habit.NextDueDate == selected.AddDays(2) && habit.ScheduledStartDate == selected), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(26, false)]
    [InlineData(12, false)]
    [InlineData(26, true)]
    public async Task Handle_ExplicitStart_ChildrenInheritResolvedParentOccurrence(int startDay, bool legacyDueDate)
    {
        var selected = new DateOnly(2026, 10, startDay);
        var today = new DateOnly(2026, 10, 20);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(today);
        _userDateService.GetUserWeekStartDayAsync(UserId, Arg.Any<CancellationToken>()).Returns(1);
        var created = new List<Habit>();
        _habitRepo.AddAsync(Arg.Do<Habit>(created.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var command = new CreateHabitCommand(UserId, "Read", null, FrequencyUnit.Day, 1,
            SubHabits: ["Chapter"], StartDate: selected, DueDate: legacyDueDate ? selected : null,
            Options: new HabitCommandOptions(Days: [DayOfWeek.Wednesday]));

        (await _handler.Handle(command, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var parent = created.Single(habit => habit.ParentHabitId is null);
        var child = created.Single(habit => habit.ParentHabitId == parent.Id);
        parent.NextDueDate.Should().Be(new DateOnly(2026, 10, startDay == 26 ? 28 : 21));
        child.NextDueDate.Should().Be(parent.NextDueDate);
        child.StartDate.Should().Be(selected);
        child.ScheduledStartDate.Should().Be(selected);
        child.Days.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public async Task Handle_HistoricalStart_ChildrenInheritResolvedTerminalState(
        bool restrictedWeekdays, bool legacyDueDate, bool terminal)
    {
        var selected = new DateOnly(2026, 10, 1);
        var today = new DateOnly(2026, 10, 20);
        var end = new DateOnly(2026, 10, terminal ? 10 : 30);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(today);
        _userDateService.GetUserWeekStartDayAsync(UserId, Arg.Any<CancellationToken>()).Returns(1);
        var created = new List<Habit>();
        _habitRepo.AddAsync(Arg.Do<Habit>(created.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var command = new CreateHabitCommand(UserId, "Read", null, FrequencyUnit.Day, 1,
            SubHabits: ["Chapter", "Notes"], StartDate: selected, DueDate: legacyDueDate ? selected : null,
            Options: new HabitCommandOptions(EndDate: end, Days: restrictedWeekdays ? [DayOfWeek.Wednesday] : null));
        new Orbit.Application.Habits.Validators.CreateHabitCommandValidator().Validate(command).IsValid.Should().BeTrue();

        (await _handler.Handle(command, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var parent = created.Single(habit => habit.ParentHabitId is null);
        parent.NextDueDate.Should().Be(new DateOnly(2026, 10, terminal
            ? restrictedWeekdays ? 14 : 11
            : restrictedWeekdays ? 21 : 20));
        parent.IsCompleted.Should().Be(terminal);
        var children = created.Where(habit => habit.ParentHabitId == parent.Id).ToList();
        children.Should().HaveCount(2);
        children.Should().AllSatisfy(child =>
        {
            child.NextDueDate.Should().Be(parent.NextDueDate);
            child.IsCompleted.Should().Be(terminal);
            child.StartDate.Should().Be(selected);
            child.ScheduledStartDate.Should().Be(selected);
            child.EndDate.Should().Be(end);
            child.FrequencyUnit.Should().Be(parent.FrequencyUnit);
            child.FrequencyQuantity.Should().Be(parent.FrequencyQuantity);
            child.Days.Should().BeEmpty();
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_LegacyDate_ChildrenRetainExistingDueDateInheritance(bool explicitDueDate)
    {
        var today = new DateOnly(2026, 10, 20);
        DateOnly? requestedDueDate = explicitDueDate ? new DateOnly(2026, 10, 26) : null;
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(today);
        var created = new List<Habit>();
        _habitRepo.AddAsync(Arg.Do<Habit>(created.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var command = new CreateHabitCommand(UserId, "Read", null, FrequencyUnit.Day, 1,
            SubHabits: ["Chapter"], DueDate: requestedDueDate,
            Options: new HabitCommandOptions(Days: [DayOfWeek.Wednesday]));

        (await _handler.Handle(command, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var parent = created.Single(habit => habit.ParentHabitId is null);
        var child = created.Single(habit => habit.ParentHabitId == parent.Id);
        parent.NextDueDate.Should().Be(new DateOnly(2026, 10, explicitDueDate ? 28 : 21));
        child.NextDueDate.Should().Be(requestedDueDate ?? parent.NextDueDate);
        child.StartDate.Should().Be(child.NextDueDate);
        child.IsCompleted.Should().BeFalse();
        child.Days.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HabitSchedule.OneTime)]
    [InlineData(HabitSchedule.Recurring)]
    [InlineData(HabitSchedule.Flexible)]
    [InlineData(HabitSchedule.General)]
    public async Task Handle_LegacyFieldsStillCreateEachSchedule(HabitSchedule schedule)
    {
        var repeating = schedule is HabitSchedule.Recurring or HabitSchedule.Flexible;
        var command = new CreateHabitCommand(
            UserId, "Legacy habit", null, repeating ? FrequencyUnit.Day : null, repeating ? 1 : null,
            DueDate: Today, IsGeneral: schedule == HabitSchedule.General,
            Options: new HabitCommandOptions(IsFlexible: schedule == HabitSchedule.Flexible));
        var validation = new Orbit.Application.Habits.Validators.CreateHabitCommandValidator().Validate(command);
        validation.IsValid.Should().BeTrue();

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _habitRepo.Received(1).AddAsync(
            Arg.Is<Habit>(habit => habit.Id == result.Value && habit.Schedule == schedule
                && habit.StartDate == Today && habit.NextDueDate == Today
                && habit.Schedule == schedule
                && habit.FrequencyUnit == command.FrequencyUnit
                && habit.FrequencyQuantity == command.FrequencyQuantity),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ValidCommand_CreatesHabitAndSaves()
    {
        var command = new CreateHabitCommand(
            UserId, "Read 30 minutes", "Daily reading", FrequencyUnit.Day, 1,
            Options: new HabitCommandOptions(EndDate: Today.AddDays(30)));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeEmpty();
        await _habitRepo.Received(1).AddAsync(
            Arg.Is<Habit>(h => h.Title == "Read 30 minutes" && h.UserId == UserId
                && h.ParentHabitId == null && h.EndDate == Today.AddDays(30)),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).AcquireAdvisoryLockAsync(
            HabitCeilingLock.ForUser(UserId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithIntervalWeeks_PersistsInterval()
    {
        var command = new CreateHabitCommand(
            UserId,
            "Run",
            null,
            FrequencyUnit.Day,
            1,
            IntervalWeeks: 2);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _habitRepo.Received(1).AddAsync(
            Arg.Is<Habit>(habit => habit.IntervalWeeks == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ValidCommand_FiresOnboardingHabitCreatedSignal()
    {
        var command = new CreateHabitCommand(UserId, "Read", null, FrequencyUnit.Day, 1);

        await _handler.Handle(command, CancellationToken.None);

        await _gamificationService.Received(1).ProcessOnboardingChecklistAsync(
            UserId, OnboardingChecklistSignal.HabitCreated, Arg.Any<CancellationToken>());
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
        var existing1 = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, "A", FrequencyUnit.Day, 1, DueDate: Today, Position: 0)).Value;
        var existing2 = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, "B", FrequencyUnit.Day, 1, DueDate: Today, Position: 5)).Value;
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
        await _habitRepo.Received(3).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithTagIds_AssignsTagsToHabit()
    {
        var tag = Tag.Create(UserId, "Health", "#00ff00").Value;

        _tagRepo.FindTrackedAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Tag> { tag });

        var command = new CreateHabitCommand(
            UserId, "Exercise", null, FrequencyUnit.Day, 1,
            TagIds: new List<Guid> { tag.Id });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _tagRepo.Received(1).FindTrackedAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ForeignTagId_ReturnsFailureWithoutCreating()
    {
        _tagRepo.FindTrackedAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Tag>());

        var command = new CreateHabitCommand(
            UserId, "Exercise", null, FrequencyUnit.Day, 1,
            TagIds: new List<Guid> { Guid.NewGuid() });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Orbit.Application.Common.ErrorCodes.TagNotFound);
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_HabitLimitReached_ReturnsCodedFailure(bool isPtBr)
    {
        var userRepository = Substitute.For<IGenericRepository<User>>();
        var appConfig = Substitute.For<IAppConfigService>();
        userRepository.GetByIdAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(User.Create("Test User", "test@example.com").Value);
        appConfig.GetAsync(AppConfigKeys.FreeMaxHabits, AppConstants.DefaultFreeMaxHabits, Arg.Any<CancellationToken>())
            .Returns(AppConstants.DefaultFreeMaxHabits);
        _habitRepo.CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(AppConstants.DefaultFreeMaxHabits);
        var repos = new CreateHabitRepositories(_habitRepo, _tagRepo, _goalRepo);
        var handler = new CreateHabitCommandHandler(
            repos, _userDateService,
            new PayGateService(_habitRepo, userRepository, appConfig, _userDateService, Substitute.For<IFeatureFlagService>()),
            _gamificationService, _goalCompletionService, _unitOfWork, _cache,
            Substitute.For<ILogger<CreateHabitCommandHandler>>());

        var command = new CreateHabitCommand(UserId, "New habit", null, FrequencyUnit.Day, 1);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorArgs.Should().Equal(1000);
        ErrorCopy.TryResolve(result.ErrorCode, isPtBr, result.ErrorArgs, out var localized).Should().BeTrue();
        localized.Should().Be(isPtBr ? "Você chegou ao limite de 1000 hábitos." : "You've reached the 1000 habit limit.");
        result.ErrorCode.Should().Be("HABIT_LIMIT_REACHED");
        result.Error.Should().Be("You've reached the 1000 habit limit.");
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
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
        var cacheKey = $"summary:{UserId}:{Today:yyyy-MM-dd}:en";
        _cache.Set(cacheKey, "cached-summary");

        var command = new CreateHabitCommand(UserId, "Test habit", null, FrequencyUnit.Day, 1);

        await _handler.Handle(command, CancellationToken.None);

        _cache.TryGetValue(cacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_WithGoalIds_LinksGoalsToHabit()
    {
        var goal = Goal.Create(UserId, "Fitness Goal", 10, "workouts").Value;

        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal });

        var command = new CreateHabitCommand(
            UserId, "Exercise", null, FrequencyUnit.Day, 1,
            GoalIds: new List<Guid> { goal.Id });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _goalRepo.Received(1).FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ForeignGoalId_ReturnsFailureWithoutCreating()
    {
        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal>());

        var command = new CreateHabitCommand(
            UserId, "Exercise", null, FrequencyUnit.Day, 1,
            GoalIds: new List<Guid> { Guid.NewGuid() });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Orbit.Application.Common.ErrorCodes.GoalNotFound);
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
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
            Arg.Is<Habit>(h => (h.Schedule == HabitSchedule.Flexible)),
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
            Arg.Is<Habit>(h => h.NextDueDate == dueDate),
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
            Arg.Is<Habit>(h => h.NextDueDate == Today),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserTimezoneAheadOfUtc_UsesUserTodayNotUtcToday()
    {
        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var userToday = utcToday.AddDays(1);
        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(userToday);

        var command = new CreateHabitCommand(
            UserId, "Timezone Habit", null, FrequencyUnit.Day, 1);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _habitRepo.Received(1).AddAsync(
            Arg.Is<Habit>(h => h.NextDueDate == userToday && h.NextDueDate != utcToday),
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
        var options = new HabitCommandOptions(
            Days: new[] { DayOfWeek.Saturday, DayOfWeek.Sunday });

        var command = new CreateHabitCommand(
            UserId, "Weekend Habit", null, FrequencyUnit.Day, 1,
            Options: options);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _habitRepo.Received(1).AddAsync(
            Arg.Is<Habit>(h => h.NextDueDate == new DateOnly(2026, 3, 21)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithDays_IncludingToday_KeepsTodayAsDueDate()
    {
        var options = new HabitCommandOptions(
            Days: new[] { DayOfWeek.Friday, DayOfWeek.Saturday });

        var command = new CreateHabitCommand(
            UserId, "Friday Habit", null, FrequencyUnit.Day, 1,
            Options: options);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _habitRepo.Received(1).AddAsync(
            Arg.Is<Habit>(h => h.NextDueDate == Today),
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
            Arg.Is<Habit>(h => (h.Schedule == HabitSchedule.General)),
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
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>());
    }
}
