using System.Reflection;
using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the pure logic in AiSummaryService: StripMarkdownFences, BuildSummaryPrompt,
/// and BuildHabitSection. The AI client call itself is an integration concern tested elsewhere.
/// </summary>
public class AiSummaryServiceTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2025, 4, 1);

    private static readonly BindingFlags PrivateStatic =
        BindingFlags.NonPublic | BindingFlags.Static;

    // ── StripMarkdownFences ──────────────────────────────────────────

    [Fact]
    public void StripMarkdownFences_PlainText_ReturnsUnchanged()
    {
        var text = "Just a plain summary.";
        AiSummaryService.StripMarkdownFences(text).Should().Be(text);
    }

    [Fact]
    public void StripMarkdownFences_WithFences_RemovesThem()
    {
        var text = "```\nHello world\n```";
        AiSummaryService.StripMarkdownFences(text).Should().Be("Hello world");
    }

    [Fact]
    public void StripMarkdownFences_WithLanguageFences_RemovesThem()
    {
        var text = "```markdown\nContent here\n```";
        AiSummaryService.StripMarkdownFences(text).Should().Be("Content here");
    }

    [Fact]
    public void StripMarkdownFences_FencesWithoutClosing_RemovesOpening()
    {
        var text = "```\nNo closing fence";
        var result = AiSummaryService.StripMarkdownFences(text);
        result.Should().Be("No closing fence");
    }

    [Fact]
    public void StripMarkdownFences_WhitespaceOnly_ReturnsTrimmed()
    {
        var text = "   \n   ";
        AiSummaryService.StripMarkdownFences(text).Should().BeEmpty();
    }

    [Fact]
    public void StripMarkdownFences_MultipleLines_PreservesContent()
    {
        var text = "```\nLine 1\nLine 2\nLine 3\n```";
        var result = AiSummaryService.StripMarkdownFences(text);
        result.Should().Contain("Line 1");
        result.Should().Contain("Line 2");
        result.Should().Contain("Line 3");
    }

    [Fact]
    public void StripMarkdownFences_LeadingTrailingWhitespace_Trims()
    {
        var text = "  \n  Some text  \n  ";
        AiSummaryService.StripMarkdownFences(text).Should().Be("Some text");
    }

    // ── BuildSummaryPrompt via reflection ──

    [Fact]
    public void BuildSummaryPrompt_English_ContainsEnglishLanguageName()
    {
        var habits = new List<Habit> { CreateHabit("Yoga") };

        var result = InvokeBuildSummaryPrompt(habits, [], Today, "en");

        result.Should().Contain("English");
        result.Should().Contain("Yoga");
    }

    [Fact]
    public void BuildSummaryPrompt_Portuguese_ContainsBrazilianPortuguese()
    {
        var habits = new List<Habit> { CreateHabit("Leitura") };

        var result = InvokeBuildSummaryPrompt(habits, [], Today, "pt-br");

        result.Should().Contain("Brazilian Portuguese");
    }

    [Fact]
    public void BuildSummaryPrompt_PtShort_AlsoMapsToPortuguese()
    {
        var habits = new List<Habit> { CreateHabit("Read") };

        var result = InvokeBuildSummaryPrompt(habits, [], Today, "pt");

        result.Should().Contain("Brazilian Portuguese");
    }

    [Fact]
    public void BuildSummaryPrompt_IncludesDateInOutput()
    {
        var habits = new List<Habit> { CreateHabit("Walk") };
        var date = new DateOnly(2025, 12, 25);

        var result = InvokeBuildSummaryPrompt(habits, [], date, "en");

        result.Should().Contain("December 25, 2025");
    }

    [Fact]
    public void BuildSummaryPrompt_WithOverdueHabits_IncludesOverdueSection()
    {
        var scheduled = new List<Habit> { CreateHabit("Exercise") };
        var overdue = new List<Habit> { CreateHabit("Meditation") };

        var result = InvokeBuildSummaryPrompt(scheduled, overdue, Today, "en");

        result.Should().Contain("Meditation");
        result.Should().NotContain("(none)");
    }

    [Fact]
    public void BuildSummaryPrompt_NoOverdueHabits_ShowsNone()
    {
        var scheduled = new List<Habit> { CreateHabit("Exercise") };

        var result = InvokeBuildSummaryPrompt(scheduled, [], Today, "en");

        result.Should().Contain("(none)");
    }

    [Fact]
    public void BuildSummaryPrompt_ShowsProgressCount()
    {
        var habits = new List<Habit>
        {
            CreateHabit("A"),
            CreateHabit("B"),
            CreateHabit("C"),
        };

        var result = InvokeBuildSummaryPrompt(habits, [], Today, "en");

        result.Should().Contain("0/3 habits completed");
    }

    // ── BuildHabitSection via reflection ──

    [Fact]
    public void BuildHabitSection_EmptyList_ReturnsNoHabitsScheduled()
    {
        var result = InvokeBuildHabitSection([]);

        result.Should().Be("(no habits scheduled)");
    }

    [Fact]
    public void BuildHabitSection_SinglePendingHabit_ShowsPending()
    {
        var habits = new List<Habit> { CreateHabit("Yoga") };

        var result = InvokeBuildHabitSection(habits);

        result.Should().Contain("Yoga (pending)");
    }

    [Fact]
    public void BuildHabitSection_ParentWithChildren_ShowsSubTaskCount()
    {
        var parent = CreateHabit("Workout");
        var child1 = CreateHabit("Push-ups", parentId: parent.Id);
        var child2 = CreateHabit("Sit-ups", parentId: parent.Id);

        var result = InvokeBuildHabitSection([parent, child1, child2]);

        result.Should().Contain("Workout (pending, 0/2 sub-tasks done)");
        result.Should().Contain("Push-ups (pending)");
        result.Should().Contain("Sit-ups (pending)");
    }

    [Fact]
    public void BuildHabitSection_ChildrenOnly_ReturnsNoHabitsScheduled()
    {
        // Children without their parent in the list should not render as top-level
        var parent = CreateHabit("Workout");
        var child = CreateHabit("Push-ups", parentId: parent.Id);

        var result = InvokeBuildHabitSection([child]);

        result.Should().Be("(no habits scheduled)");
    }

    // ── BuildSummaryPrompt additional edge cases ──

    [Fact]
    public void BuildSummaryPrompt_UnknownLanguage_DefaultsToEnglish()
    {
        var habits = new List<Habit> { CreateHabit("Test") };

        var result = InvokeBuildSummaryPrompt(habits, [], Today, "fr");

        result.Should().Contain("English");
    }

    [Fact]
    public void BuildSummaryPrompt_MultipleOverdueHabits_AllListed()
    {
        var scheduled = new List<Habit> { CreateHabit("Active") };
        var overdue = new List<Habit>
        {
            CreateHabit("Overdue1"),
            CreateHabit("Overdue2"),
            CreateHabit("Overdue3"),
        };

        var result = InvokeBuildSummaryPrompt(scheduled, overdue, Today, "en");

        result.Should().Contain("Overdue1");
        result.Should().Contain("Overdue2");
        result.Should().Contain("Overdue3");
    }

    // ── BuildSummaryPrompt with various habit types ──

    [Fact]
    public void BuildSummaryPrompt_EmptyHabitList_ShowsNoHabitsScheduled()
    {
        var result = InvokeBuildSummaryPrompt([], [], Today, "en");

        result.Should().Contain("(no habits scheduled)");
        result.Should().Contain("0/0 habits completed");
    }

    [Fact]
    public void BuildSummaryPrompt_ContainsRulesSection()
    {
        var habits = new List<Habit> { CreateHabit("Walk") };
        var result = InvokeBuildSummaryPrompt(habits, [], Today, "en");

        result.Should().Contain("Rules:");
        result.Should().Contain("2-3 short sentences");
        result.Should().Contain("Do NOT use markdown");
    }

    [Fact]
    public void BuildSummaryPrompt_UpperCaseLanguage_StillMapsCorrectly()
    {
        var habits = new List<Habit> { CreateHabit("Test") };

        // "PT-BR" should still map since the switch lowercases it
        var result = InvokeBuildSummaryPrompt(habits, [], Today, "PT-BR");

        result.Should().Contain("Brazilian Portuguese");
    }

    [Fact]
    public void BuildSummaryPrompt_EmptyLanguageString_DefaultsToEnglish()
    {
        var habits = new List<Habit> { CreateHabit("Test") };
        var result = InvokeBuildSummaryPrompt(habits, [], Today, "");

        result.Should().Contain("English");
    }

    // ── BuildHabitSection with completed habits ──

    [Fact]
    public void BuildHabitSection_MultipleParentsNoChildren_ListsAllPending()
    {
        var habits = new List<Habit>
        {
            CreateHabit("Yoga"),
            CreateHabit("Read"),
            CreateHabit("Cook"),
        };

        var result = InvokeBuildHabitSection(habits);

        result.Should().Contain("Yoga (pending)");
        result.Should().Contain("Read (pending)");
        result.Should().Contain("Cook (pending)");
    }

    [Fact]
    public void BuildHabitSection_ParentWithMultipleChildrenGroups_ShowsAllHierarchies()
    {
        var parent1 = CreateHabit("Workout");
        var child1a = CreateHabit("Push-ups", parentId: parent1.Id);
        var child1b = CreateHabit("Sit-ups", parentId: parent1.Id);

        var parent2 = CreateHabit("Study");
        var child2a = CreateHabit("Math", parentId: parent2.Id);

        var result = InvokeBuildHabitSection([parent1, child1a, child1b, parent2, child2a]);

        result.Should().Contain("Workout (pending, 0/2 sub-tasks done)");
        result.Should().Contain("Study (pending, 0/1 sub-tasks done)");
        result.Should().Contain("Push-ups (pending)");
        result.Should().Contain("Sit-ups (pending)");
        result.Should().Contain("Math (pending)");
    }

    // ── BuildSummaryPrompt with overdue section only ──

    [Fact]
    public void BuildSummaryPrompt_OnlyOverdueHabits_ScheduledSectionEmpty()
    {
        var overdue = new List<Habit> { CreateHabit("Overdue Task") };

        var result = InvokeBuildSummaryPrompt([], overdue, Today, "en");

        result.Should().Contain("(no habits scheduled)");
        result.Should().Contain("Overdue Task");
        result.Should().NotContain("(none)");
    }

    // ── StripMarkdownFences edge cases ──

    [Fact]
    public void StripMarkdownFences_OnlyFences_ReturnsEmpty()
    {
        var text = "```\n```";
        var result = AiSummaryService.StripMarkdownFences(text);
        result.Should().BeEmpty();
    }

    [Fact]
    public void StripMarkdownFences_FencesWithExtraBlankLines_StripsAndTrims()
    {
        var text = "```\n\nContent with blanks\n\n```";
        var result = AiSummaryService.StripMarkdownFences(text);
        result.Should().Contain("Content with blanks");
    }

    [Fact]
    public void StripMarkdownFences_InlineFences_NotStripped()
    {
        // Inline code fences (single backtick) should not be affected
        var text = "Use `code` for inline formatting";
        var result = AiSummaryService.StripMarkdownFences(text);
        result.Should().Be(text);
    }

    [Fact]
    public void StripMarkdownFences_FencesWithJsonLanguage_Stripped()
    {
        var text = "```json\n{\"key\": \"value\"}\n```";
        var result = AiSummaryService.StripMarkdownFences(text);
        result.Should().Be("{\"key\": \"value\"}");
    }

    // ── BuildSummaryPrompt date formatting ──

    [Fact]
    public void BuildSummaryPrompt_NewYearsDay_FormatsDateCorrectly()
    {
        var habits = new List<Habit> { CreateHabit("Celebrate") };
        var date = new DateOnly(2026, 1, 1);

        var result = InvokeBuildSummaryPrompt(habits, [], date, "en");

        result.Should().Contain("January 1, 2026");
    }

    // ── BuildSummaryPrompt progress counts ──

    [Fact]
    public void BuildSummaryPrompt_AllHabitsPresent_ShowsCorrectTotal()
    {
        var habits = new List<Habit>
        {
            CreateHabit("A"),
            CreateHabit("B"),
            CreateHabit("C"),
            CreateHabit("D"),
            CreateHabit("E"),
        };

        var result = InvokeBuildSummaryPrompt(habits, [], Today, "en");

        result.Should().Contain("0/5 habits completed");
    }

    // ── BuildHabitSection indent verification ──

    [Fact]
    public void BuildHabitSection_Children_IndentedWithTwoSpaces()
    {
        var parent = CreateHabit("Parent");
        var child = CreateHabit("Child", parentId: parent.Id);

        var result = InvokeBuildHabitSection([parent, child]);

        result.Should().Contain("  - Child (pending)");
    }

    // ── Helpers ──

    private static Habit CreateHabit(
        string title,
        Guid? parentId = null,
        DateOnly? dueDate = null)
    {
        return Habit.Create(new HabitCreateParams(
            ValidUserId,
            title,
            FrequencyUnit.Day,
            1,
            DueDate: dueDate ?? Today,
            ParentHabitId: parentId)).Value;
    }

    private static string InvokeBuildSummaryPrompt(
        List<Habit> scheduledHabits, List<Habit> overdueHabits, DateOnly date, string language)
    {
        var method = typeof(AiSummaryService)
            .GetMethod("BuildSummaryPrompt", PrivateStatic)!;
        return (string)method.Invoke(null, [scheduledHabits, overdueHabits, date, language])!;
    }

    private static string InvokeBuildHabitSection(List<Habit> scheduledHabits)
    {
        var method = typeof(AiSummaryService)
            .GetMethod("BuildHabitSection", PrivateStatic)!;
        return (string)method.Invoke(null, [scheduledHabits])!;
    }
}
