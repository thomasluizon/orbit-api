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

    [Theory]
    [MemberData(nameof(OptionalParameterCases.Fields), typeof(AssignTagsTool), "", MemberType = typeof(OptionalParameterCases))]
    public async Task OptionalNull_AssignsSameTagsAsAbsent(string field)
    {
        var habit = CreateHabit("Read");
        var tag = Tag.Create(UserId, "Work", "#ff0000").Value;
        SetupHabitFound(habit);
        _habitRepo.FindTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>()).Returns([habit]);
        _tagRepo.FindTrackedAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>()).Returns([tag]);
        _tagRepo.FindOneTrackedAsync(Arg.Any<Expression<Func<Tag, bool>>>(), cancellationToken: Arg.Any<CancellationToken>()).Returns(tag);
        var body = new Dictionary<string, object?>();
        body[field == "habit_id" ? "habit_ids" : "habit_id"] = field == "habit_id" ? new[] { habit.Id } : habit.Id;
        body[field == "tag_names" ? "tag_ids" : "tag_names"] = field == "tag_names" ? new object[] { tag.Id } : new object[] { "Work" };
        (await Execute(JsonSerializer.Serialize(body))).Success.Should().BeTrue();
        var baseline = habit.Tags.Select(value => value.Id).ToArray();
        body[field] = null;
        (await Execute(JsonSerializer.Serialize(body))).Success.Should().BeTrue();
        habit.Tags.Select(value => value.Id).Should().Equal(baseline).And.Equal(tag.Id);
    }

    [Fact]
    public async Task RoundThree_TagBatch_LoadsAndSavesOnce()
    {
        var habits = new[] { CreateHabit("One"), CreateHabit("Two") };
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns(habits);
        _habitRepo.FindTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>()).Returns(habits);
        foreach (var habit in habits)
            _habitRepo.FindOneTrackedAsync(Arg.Is<Expression<Func<Habit, bool>>>(predicate => predicate.Compile()(habit)),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>()).Returns(habit);
        var arguments = JsonSerializer.SerializeToElement(new { habit_ids = habits.Select(habit => habit.Id).ToArray(), tag_names = new[] { "Saúde" } });

        var result = await _tool.ExecuteAsync(arguments, UserId, default);

        result.Success.Should().BeTrue();
        habits.Should().OnlyContain(habit => habit.Tags.Count == 1);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _habitRepo.Received(1).FindTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignTags_ReplacesExistingTags()
    {
        var habit = CreateHabit("Run");
        var oldTag = Tag.Create(UserId, "Old", "#000000").Value;
        habit.AddTag(oldTag);
        SetupHabitFound(habit);

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
        _tagRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(),
            Arg.Any<CancellationToken>()
        ).Returns(new List<Tag> { existingTag }.AsReadOnly());

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "tag_names": ["Health"]}""");

        result.Success.Should().BeTrue();
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
    public async Task NeitherTagIdsNorTagNames_ReturnsError()
    {
        var id = Guid.NewGuid();
        var result = await Execute($$$"""{"habit_id": "{{{id}}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Provide either tag_ids or tag_names");
    }

    [Fact]
    public async Task AssignByTagIds_ReplacesExistingTags_NoAutoCreate()
    {
        var habit = CreateHabit("Run");
        var oldTag = Tag.Create(UserId, "Old", "#000000").Value;
        habit.AddTag(oldTag);
        SetupHabitFound(habit);

        var health = Tag.Create(UserId, "Health", "#ff0000").Value;
        var fitness = Tag.Create(UserId, "Fitness", "#00ff00").Value;
        _tagRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(),
            Arg.Any<CancellationToken>()
        ).Returns(new List<Tag> { health, fitness }.AsReadOnly());

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "tag_ids": ["{{{health.Id}}}", "{{{fitness.Id}}}"]}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Run");
        habit.Tags.Should().BeEquivalentTo(new[] { health, fitness });
        await _tagRepo.DidNotReceive().AddAsync(Arg.Any<Tag>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignByEmptyTagIds_RemovesAllTags()
    {
        var habit = CreateHabit("Run");
        habit.AddTag(Tag.Create(UserId, "Old", "#000000").Value);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "tag_ids": []}""");

        result.Success.Should().BeTrue();
        habit.Tags.Should().BeEmpty();
        await _tagRepo.DidNotReceive().FindTrackedAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>());
        await _tagRepo.DidNotReceive().AddAsync(Arg.Any<Tag>(), Arg.Any<CancellationToken>());
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
        await _tagRepo.Received(1).AddAsync(Arg.Any<Tag>(), Arg.Any<CancellationToken>());
    }

    private static Habit CreateHabit(string title)
    {
        return Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, title, FrequencyUnit.Day, 1, DueDate: Today)).Value;
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
