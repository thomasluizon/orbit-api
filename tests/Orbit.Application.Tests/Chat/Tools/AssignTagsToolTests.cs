using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class AssignTagsToolTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<Tag> _tagRepo = Substitute.For<IGenericRepository<Tag>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly AssignTagsTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public AssignTagsToolTests()
    {
        _tool = new AssignTagsTool(_habitRepo, _tagRepo, _unitOfWork);
    }

    [Fact]
    public async Task AssignTags_ReplacesExistingTags()
    {
        var habit = CreateHabit("Run");
        var oldTag = Tag.Create(UserId, "Old", "#000000").Value;
        habit.AddTag(oldTag);
        SetupHabitFound(habit);

        // No existing tags with new names
        _tagRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(),
            Arg.Any<Func<IQueryable<Tag>, IQueryable<Tag>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns((Tag?)null);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "tag_names": ["Health", "Fitness"]}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Run");
        await _tagRepo.Received(2).AddAsync(Arg.Any<Tag>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignExistingTags_ReusesExisting()
    {
        var habit = CreateHabit("Run");
        SetupHabitFound(habit);

        var existingTag = Tag.Create(UserId, "Health", "#ff0000").Value;
        _tagRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(),
            Arg.Any<Func<IQueryable<Tag>, IQueryable<Tag>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns(existingTag);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "tag_names": ["Health"]}""");

        result.Success.Should().BeTrue();
        // Should NOT create new tags since they already exist
        await _tagRepo.DidNotReceive().AddAsync(Arg.Any<Tag>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HabitNotFound_ReturnsError()
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns((Habit?)null);

        var id = Guid.NewGuid();
        var result = await Execute($$$"""{"habit_id": "{{{id}}}", "tag_names": ["Health"]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task EmptyTagNames_ReturnsError()
    {
        var result = await Execute($$$"""{"habit_id": "{{{Guid.NewGuid()}}}", "tag_names": []}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("At least one tag name");
    }

    [Fact]
    public async Task MissingHabitId_ReturnsError()
    {
        var result = await Execute("""{"tag_names": ["Health"]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_id is required");
    }

    [Fact]
    public async Task MissingTagNames_ReturnsError()
    {
        var id = Guid.NewGuid();
        var result = await Execute($$$"""{"habit_id": "{{{id}}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("tag_names is required");
    }

    [Fact]
    public async Task DuplicateTagNames_DeduplicatesTags()
    {
        var habit = CreateHabit("Run");
        SetupHabitFound(habit);

        _tagRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(),
            Arg.Any<Func<IQueryable<Tag>, IQueryable<Tag>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns((Tag?)null);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "tag_names": ["Health", "health", "HEALTH"]}""");

        result.Success.Should().BeTrue();
        // Should only create 1 tag because they're all the same (case-insensitive)
        await _tagRepo.Received(1).AddAsync(Arg.Any<Tag>(), Arg.Any<CancellationToken>());
    }

    private static Habit CreateHabit(string title)
    {
        return Habit.Create(new HabitCreateParams(UserId, title, FrequencyUnit.Day, 1, DueDate: Today)).Value;
    }

    private void SetupHabitFound(Habit habit)
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns(habit);
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
