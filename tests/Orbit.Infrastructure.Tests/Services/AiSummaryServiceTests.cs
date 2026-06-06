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

    private static readonly string[] BannedInstantPhrases =
    [
        "right now",
        "just woke up",
        "now that the afternoon is here",
        "as the day begins",
        "earlier today",
        "upcoming later today",
    ];

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

        var result = InvokeBuildSummaryPrompt(habits, Today, "en");

        result.Should().Contain("English");
        result.Should().Contain("Yoga");
    }

    [Fact]
    public void BuildSummaryPrompt_Portuguese_ContainsBrazilianPortuguese()
    {
        var habits = new List<Habit> { CreateHabit("Leitura") };

        var result = InvokeBuildSummaryPrompt(habits, Today, "pt-br");

        result.Should().Contain("Brazilian Portuguese");
    }

    [Fact]
    public void BuildSummaryPrompt_PtShort_AlsoMapsToPortuguese()
    {
        var habits = new List<Habit> { CreateHabit("Read") };

        var result = InvokeBuildSummaryPrompt(habits, Today, "pt");

        result.Should().Contain("Brazilian Portuguese");
    }

    [Fact]
    public void BuildSummaryPrompt_IncludesDateInOutput()
    {
        var habits = new List<Habit> { CreateHabit("Walk") };
        var date = new DateOnly(2025, 12, 25);

        var result = InvokeBuildSummaryPrompt(habits, date, "en");

        result.Should().Contain("December 25, 2025");
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

        var result = InvokeBuildSummaryPrompt(habits, Today, "en");

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

    [Fact]
    public void BuildHabitSection_HabitWithDueTime_ShowsAbsoluteDueTime()
    {
        var habits = new List<Habit> { CreateHabit("Workout", dueTime: new TimeOnly(14, 0)) };

        var result = InvokeBuildHabitSection(habits);

        result.Should().Contain("due 14:00");
    }

    // ── BuildSummaryPrompt additional edge cases ──

    [Fact]
    public void BuildSummaryPrompt_UnknownLanguage_DefaultsToEnglish()
    {
        var habits = new List<Habit> { CreateHabit("Test") };

        var result = InvokeBuildSummaryPrompt(habits, Today, "fr");

        result.Should().Contain("English");
    }

    // ── BuildSummaryPrompt with various habit types ──

    [Fact]
    public void BuildSummaryPrompt_EmptyHabitList_ShowsNoHabitsScheduled()
    {
        var result = InvokeBuildSummaryPrompt([], Today, "en");

        result.Should().Contain("(no habits scheduled)");
        result.Should().Contain("0/0 habits completed");
    }

    [Fact]
    public void BuildSummaryPrompt_EstablishesCloseFriendPersonaAndLeadWithDone()
    {
        var habits = new List<Habit> { CreateHabit("Walk") };
        var result = InvokeBuildSummaryPrompt(habits, Today, "en");

        result.Should().Contain("Rules:");
        result.Should().Contain("LEAD with a specific");
        result.Should().Contain("ALREADY completed today");
        result.Should().Contain("friend who actually knows you");
        result.Should().Contain("Do NOT use markdown");
    }

    [Fact]
    public void BuildSummaryPrompt_ContextDoesNotSeedBannedInstantPhrases()
    {
        var habits = new List<Habit>
        {
            CreateHabit("Morning routine"),
            CreateHabit("Evening walk", dueTime: new TimeOnly(20, 0)),
        };

        var result = InvokeBuildSummaryPrompt(habits, Today, "en", new TimeOnly(19, 0));

        // The rules section deliberately NAMES these phrases to ban them; the model-facing
        // context above it (date, time window, habit lines) must never seed them.
        var context = result[..result.IndexOf("Rules:", StringComparison.Ordinal)];

        foreach (var phrase in BannedInstantPhrases)
            context.Should().NotContain(phrase, $"the prompt context must not seed the instant-phrase \"{phrase}\"");
    }

    [Fact]
    public void BuildSummaryPrompt_RulesBanEveryInstantPhrase()
    {
        var habits = new List<Habit> { CreateHabit("Walk") };

        var result = InvokeBuildSummaryPrompt(habits, Today, "en", new TimeOnly(14, 0));

        var rules = result[result.IndexOf("Rules:", StringComparison.Ordinal)..];
        foreach (var phrase in BannedInstantPhrases)
            rules.Should().Contain(phrase, $"the rules must explicitly ban \"{phrase}\"");
    }

    [Fact]
    public void BuildSummaryPrompt_PeriodRobustTimeContext_UsesRangeNotExactMinute()
    {
        var habits = new List<Habit> { CreateHabit("Evening walk") };

        var result = InvokeBuildSummaryPrompt(habits, Today, "en", new TimeOnly(19, 0));

        result.Should().Contain("Current part of day: evening (~5pm-9pm)");
        result.Should().NotContain("19:00");
    }

    [Fact]
    public void BuildSummaryPrompt_PeriodRobustTimeContext_StartOfPeriodMatchesEnd()
    {
        var habits = new List<Habit> { CreateHabit("Afternoon focus") };

        var startOfAfternoon = InvokeBuildSummaryPrompt(habits, Today, "en", new TimeOnly(11, 0));
        var endOfAfternoon = InvokeBuildSummaryPrompt(habits, Today, "en", new TimeOnly(16, 59));

        startOfAfternoon.Should().Contain("afternoon (~11am-5pm)");
        endOfAfternoon.Should().Contain("afternoon (~11am-5pm)");
    }

    [Fact]
    public void BuildSummaryPrompt_UpperCaseLanguage_StillMapsCorrectly()
    {
        var habits = new List<Habit> { CreateHabit("Test") };

        // "PT-BR" should still map since the switch lowercases it
        var result = InvokeBuildSummaryPrompt(habits, Today, "PT-BR");

        result.Should().Contain("Brazilian Portuguese");
    }

    [Fact]
    public void BuildSummaryPrompt_EmptyLanguageString_DefaultsToEnglish()
    {
        var habits = new List<Habit> { CreateHabit("Test") };
        var result = InvokeBuildSummaryPrompt(habits, Today, "");

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

        var result = InvokeBuildSummaryPrompt(habits, date, "en");

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

        var result = InvokeBuildSummaryPrompt(habits, Today, "en");

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
        DateOnly? dueDate = null,
        TimeOnly? dueTime = null)
    {
        return Habit.Create(new HabitCreateParams(
            ValidUserId,
            title,
            FrequencyUnit.Day,
            1,
            DueDate: dueDate ?? Today,
            DueTime: dueTime,
            ParentHabitId: parentId)).Value;
    }

    private static string InvokeBuildSummaryPrompt(
        List<Habit> scheduledHabits,
        DateOnly date,
        string language,
        TimeOnly? currentLocalTime = null)
    {
        var method = typeof(AiSummaryService)
            .GetMethod("BuildSummaryPrompt", PrivateStatic)!;
        return (string)method.Invoke(null, [scheduledHabits, date, date, language, currentLocalTime])!;
    }

    private static string InvokeBuildHabitSection(List<Habit> scheduledHabits)
    {
        var method = typeof(AiSummaryService)
            .GetMethod("BuildHabitSection", PrivateStatic)!;
        return (string)method.Invoke(null, [scheduledHabits, Today, Today])!;
    }
}
