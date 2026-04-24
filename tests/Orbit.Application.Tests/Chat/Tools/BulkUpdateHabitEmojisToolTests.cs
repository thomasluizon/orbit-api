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

public class BulkUpdateHabitEmojisToolTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly BulkUpdateHabitEmojisTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public BulkUpdateHabitEmojisToolTests()
    {
        _tool = new BulkUpdateHabitEmojisTool(_habitRepo);
    }

    [Fact]
    public async Task InferFromTitle_UpdatesAllActiveHabitsWithSensibleEmojis()
    {
        var gym = CreateHabit("Go to the gym");
        var read = CreateHabit("Read a book");
        SetupHabits(gym, read);

        var result = await Execute("""{"infer_from_title": true}""");

        result.Success.Should().BeTrue();
        gym.Emoji.Should().Be("🏋️");
        read.Emoji.Should().Be("📚");
        result.EntityName.Should().Contain("2 habit");
    }

    [Fact]
    public async Task SpecificEmoji_AppliesToSelectedHabits()
    {
        var gym = CreateHabit("Gym");
        var read = CreateHabit("Read");
        SetupHabits(gym, read);

        var result = await Execute($$$"""{"habit_ids": ["{{{gym.Id}}}"], "emoji": "✅", "infer_from_title": false}""");

        result.Success.Should().BeTrue();
        gym.Emoji.Should().Be("✅");
        read.Emoji.Should().BeNull();
    }

    [Fact]
    public async Task NullEmoji_ClearsSelectedHabits()
    {
        var gym = CreateHabit("Gym", emoji: "🏋️");
        SetupHabits(gym);

        var result = await Execute($$$"""{"habit_ids": ["{{{gym.Id}}}"], "emoji": null, "infer_from_title": false}""");

        result.Success.Should().BeTrue();
        gym.Emoji.Should().BeNull();
    }

    [Fact]
    public async Task Default_ExcludesCompletedHabits()
    {
        var active = CreateHabit("Run");
        var completed = Habit.Create(new HabitCreateParams(UserId, "Old task", null, null, DueDate: Today)).Value;
        completed.Log(Today);
        SetupHabits(active, completed);

        var result = await Execute("""{"infer_from_title": true}""");

        result.Success.Should().BeTrue();
        active.Emoji.Should().Be("🏃");
        completed.Emoji.Should().BeNull();
    }

    [Fact]
    public async Task NoMatchingHabits_ReturnsError()
    {
        SetupHabits();

        var result = await Execute("""{"infer_from_title": true}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("No matching habits");
    }

    [Fact]
    public async Task EmptyHabitIds_ReturnsErrorWithoutUpdatingAllHabits()
    {
        var gym = CreateHabit("Gym");
        SetupHabits(gym);

        var result = await Execute("""{"habit_ids": [], "infer_from_title": true}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_ids");
        gym.Emoji.Should().BeNull();
        await _habitRepo.DidNotReceiveWithAnyArgs().FindTrackedAsync(default!, default);
    }

    [Fact]
    public async Task InvalidHabitIds_ReturnsErrorWithoutUpdatingAllHabits()
    {
        var gym = CreateHabit("Gym");
        SetupHabits(gym);

        var result = await Execute("""{"habit_ids": ["not-a-guid"], "infer_from_title": true}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_ids");
        gym.Emoji.Should().BeNull();
        await _habitRepo.DidNotReceiveWithAnyArgs().FindTrackedAsync(default!, default);
    }

    private static Habit CreateHabit(string title, string? emoji = null)
    {
        return Habit.Create(new HabitCreateParams(UserId, title, FrequencyUnit.Day, 1, DueDate: Today, Emoji: emoji)).Value;
    }

    private void SetupHabits(params Habit[] habits)
    {
        _habitRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>()
        ).Returns(callInfo =>
        {
            var predicate = callInfo.ArgAt<Expression<Func<Habit, bool>>>(0).Compile();
            return habits.Where(predicate).ToList();
        });
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
