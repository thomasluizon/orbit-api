using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class CreateHabitToolTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<Tag> _tagRepo = Substitute.For<IGenericRepository<Tag>>();
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly CreateHabitTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public CreateHabitToolTests()
    {
        _tool = new CreateHabitTool(_habitRepo, _tagRepo, _goalRepo, _userDateService, _payGate, _unitOfWork);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _payGate.CanCreateHabits(UserId, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _payGate.CanCreateSubHabits(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
    }

    [Fact]
    public async Task SuccessfulCreation_ReturnsSuccessWithTitleAndId()
    {
        var result = await Execute("""{"title": "Water"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Water");
        result.EntityId.Should().NotBeNullOrEmpty();
        await _habitRepo.Received(1).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingTitle_ReturnsError()
    {
        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("title is required");
    }

    [Fact]
    public async Task EmptyTitle_ReturnsError()
    {
        var result = await Execute("""{"title": "  "}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("title is required");
    }

    [Fact]
    public async Task WithFrequency_CreatesRecurringHabit()
    {
        var result = await Execute("""{"title": "Exercise", "frequency_unit": "Day", "frequency_quantity": 1}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Exercise");
    }

    [Fact]
    public async Task InvalidFrequencyUnit_CreatesOneTimeTask()
    {
        var result = await Execute("""{"title": "Task", "frequency_unit": "InvalidUnit"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Task");
    }

    [Fact]
    public async Task WithTags_AssignsTagsToHabit()
    {
        _tagRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(),
            Arg.Any<Func<IQueryable<Tag>, IQueryable<Tag>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns((Tag?)null);

        var result = await Execute("""{"title": "Run", "tag_names": ["Health", "Fitness"]}""");

        result.Success.Should().BeTrue();
        await _tagRepo.Received(2).AddAsync(Arg.Any<Tag>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithExistingTags_ReusesExistingTags()
    {
        var existingTag = Tag.Create(UserId, "Health", "#ff0000").Value;
        _tagRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(),
            Arg.Any<Func<IQueryable<Tag>, IQueryable<Tag>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns(existingTag);

        var result = await Execute("""{"title": "Run", "tag_names": ["Health"]}""");

        result.Success.Should().BeTrue();
        await _tagRepo.DidNotReceive().AddAsync(Arg.Any<Tag>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithSubHabits_CreatesParentAndChildren()
    {
        var result = await Execute("""
        {
            "title": "Before Bed",
            "frequency_unit": "Day",
            "sub_habits": [
                {"title": "Brush teeth"},
                {"title": "Floss"}
            ]
        }
        """);

        result.Success.Should().BeTrue();
        // Parent + 2 children = 3 AddAsync calls
        await _habitRepo.Received(3).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithGoals_LinksGoalsToHabit()
    {
        var goal = Goal.Create(UserId, "Be Healthy", 1, "goal").Value;
        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<CancellationToken>()
        ).Returns(new List<Goal> { goal });

        var result = await Execute($$$"""{"title": "Run", "goal_ids": ["{{{goal.Id}}}"]}""");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task WithChecklist_CreatesHabitWithChecklist()
    {
        var result = await Execute("""
        {
            "title": "Morning Routine",
            "checklist_items": [
                {"text": "Drink water"},
                {"text": "Stretch", "is_checked": true}
            ]
        }
        """);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Morning Routine");
    }

    [Fact]
    public async Task PayGateFails_ReturnsError()
    {
        _payGate.CanCreateHabits(UserId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Habit limit reached."));

        var result = await Execute("""{"title": "New Habit"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Habit limit reached");
    }

    [Fact]
    public async Task WithDueDate_UsesProvidedDate()
    {
        var result = await Execute("""{"title": "Future Task", "due_date": "2026-05-01"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Future Task");
    }

    [Fact]
    public async Task WithBadHabit_CreatesAsBadHabit()
    {
        var result = await Execute("""{"title": "Smoking", "is_bad_habit": true}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Smoking");
    }

    // ── Frequency types ──

    [Fact]
    public async Task WithDailyFrequency_CreatesRecurringHabit()
    {
        var result = await Execute("""{"title": "Meditate", "frequency_unit": "Day", "frequency_quantity": 1}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Meditate");
    }

    [Fact]
    public async Task WithWeeklyFrequency_CreatesRecurringHabit()
    {
        var result = await Execute("""{"title": "Weekly Review", "frequency_unit": "Week", "frequency_quantity": 1}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Weekly Review");
    }

    [Fact]
    public async Task WithMonthlyFrequency_CreatesRecurringHabit()
    {
        var result = await Execute("""{"title": "Pay Rent", "frequency_unit": "Month", "frequency_quantity": 1}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Pay Rent");
    }

    [Fact]
    public async Task WithYearlyFrequency_CreatesRecurringHabit()
    {
        var result = await Execute("""{"title": "Annual Checkup", "frequency_unit": "Year", "frequency_quantity": 1}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Annual Checkup");
    }

    [Fact]
    public async Task WithEveryNDays_CreatesRecurringHabit()
    {
        var result = await Execute("""{"title": "Laundry", "frequency_unit": "Day", "frequency_quantity": 3}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Laundry");
    }

    // ── Days array ──

    [Fact]
    public async Task WithDaysArray_CreatesHabitWithSpecificDays()
    {
        var result = await Execute("""
        {
            "title": "Gym",
            "frequency_unit": "Day",
            "frequency_quantity": 1,
            "days": ["Monday", "Wednesday", "Friday"]
        }
        """);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Gym");
    }

    // ── Due time ──

    [Fact]
    public async Task WithDueTime_CreatesHabitWithTime()
    {
        var result = await Execute("""{"title": "Morning Run", "due_time": "06:30"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Morning Run");
    }

    // ── End date ──

    [Fact]
    public async Task WithEndDate_CreatesRecurringHabitWithEndDate()
    {
        var result = await Execute("""
        {
            "title": "Summer Challenge",
            "frequency_unit": "Day",
            "frequency_quantity": 1,
            "end_date": "2026-08-31"
        }
        """);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Summer Challenge");
    }

    // ── Description ──

    [Fact]
    public async Task WithDescription_CreatesHabitWithDescription()
    {
        var result = await Execute("""{"title": "Read", "description": "Read at least 20 pages"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Read");
    }

    // ── Reminder times ──

    [Fact]
    public async Task WithReminderTimes_CreatesHabitWithReminders()
    {
        var result = await Execute("""
        {
            "title": "Take Meds",
            "due_time": "08:00",
            "reminder_enabled": true,
            "reminder_times": [15, 60]
        }
        """);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Take Meds");
    }

    // ── Flexible habit ──

    [Fact]
    public async Task WithFlexible_CreatesFlexibleHabit()
    {
        var result = await Execute("""
        {
            "title": "Yoga",
            "frequency_unit": "Week",
            "frequency_quantity": 3,
            "is_flexible": true
        }
        """);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Yoga");
    }

    // ── Scheduled reminders ──

    [Fact]
    public async Task WithScheduledReminders_CreatesHabitWithScheduledReminders()
    {
        var result = await Execute("""
        {
            "title": "Appointment",
            "scheduled_reminders": [
                {"when": "same_day", "time": "09:00"},
                {"when": "day_before", "time": "20:00"}
            ]
        }
        """);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Appointment");
    }

    // ── Multiple tags and goals ──

    [Fact]
    public async Task WithMultipleTagsAndGoals_CreatesAndAssignsAll()
    {
        _tagRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(),
            Arg.Any<Func<IQueryable<Tag>, IQueryable<Tag>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns((Tag?)null);

        var goal1 = Goal.Create(UserId, "Goal A", 1, "goal").Value;
        var goal2 = Goal.Create(UserId, "Goal B", 1, "goal").Value;
        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<CancellationToken>()
        ).Returns(new List<Goal> { goal1, goal2 });

        var result = await Execute($$$"""
        {
            "title": "Full Habit",
            "tag_names": ["Health", "Fitness", "Morning"],
            "goal_ids": ["{{{goal1.Id}}}", "{{{goal2.Id}}}"]
        }
        """);

        result.Success.Should().BeTrue();
        await _tagRepo.Received(3).AddAsync(Arg.Any<Tag>(), Arg.Any<CancellationToken>());
    }

    // ── Checklist items with is_checked ──

    [Fact]
    public async Task WithChecklistMixedCheckedState_CreatesHabit()
    {
        var result = await Execute("""
        {
            "title": "Evening Routine",
            "checklist_items": [
                {"text": "Brush teeth", "is_checked": false},
                {"text": "Floss", "is_checked": true},
                {"text": "Wash face"}
            ]
        }
        """);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Evening Routine");
    }

    // ── Sub-habits with custom properties ──

    [Fact]
    public async Task WithSubHabitsWithFrequency_InheritsParentFrequency()
    {
        var result = await Execute("""
        {
            "title": "Workout",
            "frequency_unit": "Day",
            "frequency_quantity": 1,
            "sub_habits": [
                {"title": "Push-ups"},
                {"title": "Sit-ups", "frequency_unit": "Day", "frequency_quantity": 2}
            ]
        }
        """);

        result.Success.Should().BeTrue();
        await _habitRepo.Received(3).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubHabitPayGateFails_ReturnsError()
    {
        _payGate.CanCreateSubHabits(UserId, Arg.Any<CancellationToken>())
            .Returns(Domain.Common.Result.Failure("Sub-habit limit reached."));

        var result = await Execute("""
        {
            "title": "Parent",
            "frequency_unit": "Day",
            "sub_habits": [{"title": "Child"}]
        }
        """);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Sub-habit limit reached");
    }

    // ── Bad habit with slip alert ──

    [Fact]
    public async Task BadHabitWithSlipAlert_CreatesWithSlipAlertEnabled()
    {
        var result = await Execute("""
        {
            "title": "Nail Biting",
            "is_bad_habit": true,
            "slip_alert_enabled": true
        }
        """);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Nail Biting");
    }

    // ── Duplicate tag names deduplication ──

    [Fact]
    public async Task WithDuplicateTagNames_DeduplicatesTags()
    {
        _tagRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(),
            Arg.Any<Func<IQueryable<Tag>, IQueryable<Tag>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns((Tag?)null);

        var result = await Execute("""{"title": "Run", "tag_names": ["Health", "health", "HEALTH"]}""");

        result.Success.Should().BeTrue();
        // Only one tag should be created due to case-insensitive dedup
        await _tagRepo.Received(1).AddAsync(Arg.Any<Tag>(), Arg.Any<CancellationToken>());
    }

    // ── Empty tag names filtered ──

    [Fact]
    public async Task WithEmptyTagNames_FiltersThem()
    {
        var result = await Execute("""{"title": "Run", "tag_names": ["", "  ", "Valid"]}""");

        result.Success.Should().BeTrue();
    }

    // ── Empty goal_ids list ──

    [Fact]
    public async Task WithEmptyGoalIds_SkipsGoalLinking()
    {
        var result = await Execute("""{"title": "Run", "goal_ids": []}""");

        result.Success.Should().BeTrue();
        await _goalRepo.DidNotReceive().FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<CancellationToken>());
    }

    // ── Invalid goal_ids ──

    [Fact]
    public async Task WithInvalidGoalIds_SkipsGoalLinking()
    {
        var result = await Execute("""{"title": "Run", "goal_ids": ["not-a-guid"]}""");

        result.Success.Should().BeTrue();
    }

    // ── Frequency unit defaults quantity to 1 ──

    [Fact]
    public async Task FrequencyUnitWithoutQuantity_DefaultsToOne()
    {
        var result = await Execute("""{"title": "Daily Walk", "frequency_unit": "Day"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Daily Walk");
    }

    // ── Sub-habit with checklist ──

    [Fact]
    public async Task SubHabitWithChecklist_CreatesSubWithChecklist()
    {
        var result = await Execute("""
        {
            "title": "Morning Prep",
            "frequency_unit": "Day",
            "sub_habits": [
                {
                    "title": "Breakfast",
                    "checklist_items": [
                        {"text": "Eggs"},
                        {"text": "Toast"}
                    ]
                }
            ]
        }
        """);

        result.Success.Should().BeTrue();
        await _habitRepo.Received(2).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    // ── Sub-habit with bad habit flag ──

    [Fact]
    public async Task SubHabitWithBadHabit_CreatesWithFlag()
    {
        var result = await Execute("""
        {
            "title": "Vices Tracker",
            "frequency_unit": "Day",
            "sub_habits": [
                {"title": "Smoking", "is_bad_habit": true}
            ]
        }
        """);

        result.Success.Should().BeTrue();
        await _habitRepo.Received(2).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    // ── Sub-habit with custom due date ──

    [Fact]
    public async Task SubHabitWithDueDate_UsesSubDueDate()
    {
        var result = await Execute("""
        {
            "title": "Project",
            "frequency_unit": "Day",
            "sub_habits": [
                {"title": "Phase 1", "due_date": "2026-06-01"}
            ]
        }
        """);

        result.Success.Should().BeTrue();
        await _habitRepo.Received(2).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    // ── Full combined creation ──

    [Fact]
    public async Task FullHabitWithAllOptions_CreatesSuccessfully()
    {
        _tagRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(),
            Arg.Any<Func<IQueryable<Tag>, IQueryable<Tag>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns((Tag?)null);

        var result = await Execute("""
        {
            "title": "Complete Habit",
            "description": "A fully configured habit",
            "frequency_unit": "Day",
            "frequency_quantity": 1,
            "days": ["Monday", "Wednesday"],
            "due_date": "2026-04-10",
            "end_date": "2026-12-31",
            "due_time": "14:30",
            "is_bad_habit": false,
            "is_flexible": false,
            "reminder_enabled": true,
            "reminder_times": [10, 30],
            "tag_names": ["Productivity"],
            "checklist_items": [
                {"text": "Step 1"},
                {"text": "Step 2", "is_checked": true}
            ],
            "scheduled_reminders": [
                {"when": "same_day", "time": "08:00"}
            ]
        }
        """);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Complete Habit");
        result.EntityId.Should().NotBeNullOrEmpty();
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
