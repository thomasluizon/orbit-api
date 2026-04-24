using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Services.Prompts;
using Orbit.Infrastructure.Services.Prompts.Sections.Dynamic;

namespace Orbit.Infrastructure.Tests.Services;

public class ActiveHabitsSectionTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private readonly ActiveHabitsSection _sut = new();

    private static Habit CreateHabit(
        string title,
        bool isCompleted = false,
        Guid? parentId = null,
        bool isBadHabit = false,
        bool isGeneral = false,
        DateOnly? dueDate = null,
        string? emoji = null)
    {
        var effectiveDueDate = dueDate ?? Today;

        if (isGeneral)
        {
            return Habit.Create(new HabitCreateParams(
                ValidUserId, title, null, null,
                DueDate: effectiveDueDate,
                ParentHabitId: parentId,
                IsGeneral: true,
                Emoji: emoji)).Value;
        }

        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, title, FrequencyUnit.Day, 1,
            IsBadHabit: isBadHabit,
            DueDate: effectiveDueDate,
            ParentHabitId: parentId,
            Emoji: emoji)).Value;

        if (isCompleted)
            habit.Log(effectiveDueDate);

        return habit;
    }

    private static PromptContext CreateContext(
        IReadOnlyList<Habit>? habits = null,
        DateOnly? userToday = null,
        bool useDefaultToday = true)
    {
        return new PromptContext(
            ActiveHabits: habits ?? [],
            UserFacts: [],
            HasImage: false,
            RoutinePatterns: null,
            UserTags: null,
            UserToday: useDefaultToday ? (userToday ?? Today) : userToday,
            HabitMetrics: null);
    }

    [Fact]
    public void ShouldInclude_AlwaysReturnsTrue()
    {
        var context = CreateContext();
        _sut.ShouldInclude(context).Should().BeTrue();
    }

    [Fact]
    public void Order_Returns300()
    {
        _sut.Order.Should().Be(300);
    }

    [Fact]
    public void Build_EmptyHabits_ShowsNoHabitsMessage()
    {
        // Arrange
        var context = CreateContext(habits: []);

        // Act
        var result = _sut.Build(context);

        // Assert
        result.Should().Contain("0 total");
    }

    [Fact]
    public void Build_WithHabits_IncludesHabitTitles()
    {
        // Arrange
        var habits = new List<Habit>
        {
            CreateHabit("Morning Routine"),
            CreateHabit("Exercise")
        };
        var context = CreateContext(habits: habits);

        // Act
        var result = _sut.Build(context);

        // Assert
        result.Should().Contain("Morning Routine");
        result.Should().Contain("Exercise");
    }

    [Fact]
    public void Build_WithEmoji_IncludesEmojiLabel()
    {
        var habit = CreateHabit("Gym", emoji: "💪");
        var context = CreateContext(habits: [habit]);

        var result = _sut.Build(context);

        result.Should().Contain("Emoji: 💪");
    }

    [Fact]
    public void Build_WithParentAndChildren_ShowsHierarchy()
    {
        // Arrange
        var parent = CreateHabit("Fitness");
        var child = CreateHabit("Push-ups", parentId: parent.Id);
        var habits = new List<Habit> { parent, child };
        var context = CreateContext(habits: habits);

        // Act
        var result = _sut.Build(context);

        // Assert
        result.Should().Contain("Fitness");
        result.Should().Contain("Push-ups");
    }

    [Fact]
    public void Build_CompletedHabit_IsExcludedFromIndex()
    {
        // Arrange -- use a one-time task, which becomes IsCompleted when logged
        var oneTimeHabit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Done Task", null, null,
            DueDate: Today)).Value;
        oneTimeHabit.Log(Today);

        var context = CreateContext(habits: [oneTimeHabit]);

        // Act
        var result = _sut.Build(context);

        // Assert
        result.Should().NotContain("Done Task");
        result.Should().Contain("0 total");
    }

    [Fact]
    public void Build_CompletedParentWithActiveChild_KeepsHierarchyInIndex()
    {
        // Arrange
        var parent = Habit.Create(new HabitCreateParams(
            ValidUserId, "Fitness", null, null,
            DueDate: Today)).Value;
        parent.Log(Today);
        var child = CreateHabit("Push-ups", parentId: parent.Id);
        var context = CreateContext(habits: [parent, child]);

        // Act
        var result = _sut.Build(context);

        // Assert
        result.Should().Contain("Fitness");
        result.Should().Contain("Push-ups");
        result.Should().Contain("COMPLETED");
        result.Should().Contain("1 total");
    }

    [Fact]
    public void Build_BadHabit_ShowsBadLabel()
    {
        // Arrange
        var habit = CreateHabit("Smoking", isBadHabit: true);
        var context = CreateContext(habits: [habit]);

        // Act
        var result = _sut.Build(context);

        // Assert
        result.Should().Contain("BAD");
    }

    [Fact]
    public void Build_GeneralHabit_ShowsGeneralLabel()
    {
        // Arrange
        var habit = CreateHabit("Read Books", isGeneral: true);
        var context = CreateContext(habits: [habit]);

        // Act
        var result = _sut.Build(context);

        // Assert
        result.Should().Contain("GENERAL");
    }

    [Fact]
    public void Build_OverdueHabit_ShowsOverdueLabel()
    {
        // Arrange
        var habit = CreateHabit("Overdue", dueDate: Today.AddDays(-3));
        var context = CreateContext(habits: [habit], userToday: Today);

        // Act
        var result = _sut.Build(context);

        // Assert
        result.Should().Contain("OVERDUE");
    }

    [Fact]
    public void Build_HabitDueToday_ShowsTodayLabel()
    {
        // Arrange
        var habit = CreateHabit("Due Today", dueDate: Today);
        var context = CreateContext(habits: [habit], userToday: Today);

        // Act
        var result = _sut.Build(context);

        // Assert
        result.Should().Contain("TODAY");
    }

    [Fact]
    public void Build_NullUserToday_OmitsTodayAndOverdueLabels()
    {
        // Arrange
        var habit = CreateHabit("Some Habit", dueDate: Today.AddDays(-3));
        var context = CreateContext(habits: [habit], userToday: null, useDefaultToday: false);

        // Act
        var result = _sut.Build(context);

        // Assert
        result.Should().NotContain("TODAY");
        result.Should().NotContain("OVERDUE");
    }

    [Fact]
    public void Build_LongTitle_TruncatesTo100Chars()
    {
        // Arrange
        var longTitle = new string('A', 150);
        var habit = CreateHabit(longTitle);
        var context = CreateContext(habits: [habit]);

        // Act
        var result = _sut.Build(context);

        // Assert
        // The title should be truncated to 100 characters in the output
        result.Should().Contain(new string('A', 97) + "...");
        result.Should().NotContain(new string('A', 101));
    }

    [Fact]
    public void Build_IncludesQueryInstructions()
    {
        // Arrange
        var context = CreateContext(habits: [CreateHabit("Test")]);

        // Act
        var result = _sut.Build(context);

        // Assert
        result.Should().Contain("query_habits");
        result.Should().Contain("create_habit");
    }

    [Fact]
    public void Build_CountsSummary_IncludesGeneralDueTodayOverdue()
    {
        // Arrange
        var generalHabit = CreateHabit("Read", isGeneral: true);
        var todayHabit = CreateHabit("Exercise", dueDate: Today);
        var overdueHabit = CreateHabit("Meditate", dueDate: Today.AddDays(-2));
        var habits = new List<Habit> { generalHabit, todayHabit, overdueHabit };
        var context = CreateContext(habits: habits, userToday: Today);

        // Act
        var result = _sut.Build(context);

        // Assert
        result.Should().Contain("3 total");
        result.Should().Contain("1 general");
        result.Should().Contain("1 due today");
        result.Should().Contain("1 overdue");
    }
}
