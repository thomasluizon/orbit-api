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

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
