using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class SystemPromptBuilderTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Build_RoutinesChoosePreviewWithoutStructureOrCadenceQuestions(bool blockFirst, bool hasImage)
    {
        var builder = new SystemPromptBuilder();
        var request = new PromptBuildRequest([], hasImage, UserToday: new DateOnly(2026, 4, 3),
            BlockFirstComposition: blockFirst);
        var prompt = builder.BuildStatic(request) + builder.BuildDynamic(request);

        prompt.Should().Contain("one parent habit with sub_habits");
        prompt.Should().Contain("morning or evening routine implies Day/1");
        prompt.Should().Contain("Only ask when no usable preview can be proposed");
        prompt.Should().NotContain("Ask the user which they prefer if unclear");
        prompt.Should().NotContain("checklist-vs-subhabit unclear");
        prompt.Should().NotContain("Prefer to ask the schedule INLINE");
        prompt.Should().NotContain("without a specific hour");
        prompt.Should().NotContain("the ideal flow is for you to ask first");
        prompt.Should().NotMatchRegex(@"(?i)(?<!not )(?<!never )\b(?:ask|clarify|confirm) (?:the (?:user|person) )?(?:about |for )?(?:its |their |the |a )?(?:routine(?:'s)? )?(?:cadence|structure|frequency|schedule)\b");
        prompt.Should().NotMatchRegex(@"(?i)\b(?:how often should (?:this|the|your) .*routine|what (?:cadence|schedule) (?:should|would) .*routine|(?:checklist or sub-habits|checklist versus children)\?)");
        prompt.Should().NotMatchRegex(@"(?i)\broutine[^\r\n.]*(?:->|\bthen|\bmust|\bshould)\s+ask\b");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_FormatUsesNegotiatedComposition(bool blockFirst)
    {
        var prompt = new SystemPromptBuilder().BuildDynamic(new PromptBuildRequest([], UserToday: new DateOnly(2026, 4, 3), BlockFirstComposition: blockFirst));
        if (blockFirst)
        {
            prompt.Should().Contain("Successful data answers lead with their typed block");
            prompt.Should().NotContain("Use bullet points with newlines for readability");
        }
        else
            prompt.Should().Contain("Use bullet points with newlines for readability");
    }

    private static readonly Guid TestUserId = Guid.NewGuid();

    [Fact]
    public void Build_RequiresCurrentTurnToolFactsAcrossStaticAndDynamicSections()
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, TestUserId, "Meditação", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 4, 3))).Value;
        var request = new PromptBuildRequest([habit], UserToday: new DateOnly(2026, 4, 3));
        var builder = new SystemPromptBuilder();
        var prompt = builder.BuildStatic(request) + builder.BuildDynamic(request);

        prompt.Should().Contain("GROUNDING INVARIANT");
        prompt.Should().Contain("only when a successful tool returned it in the CURRENT TURN");
        prompt.Should().Contain("set date and omit is_completed to include entries already done that day");
        prompt.Should().Contain("Never estimate or recall facts from earlier turns");
        prompt.Should().Contain("call the appropriate tool or say the data is unavailable");
        prompt.Should().Contain("Call query_habits before making factual claims");
        prompt.Should().NotContain("Answer listing and schedule questions directly from it");
        prompt.Should().NotContain("Never call query_habits or query_goals to re-fetch");
    }

    private static string BuildPrompt(
        IReadOnlyList<Habit> habits,
        bool hasImage = false, IReadOnlyList<Tag>? userTags = null,
        DateOnly? userToday = null, IReadOnlyDictionary<Guid, HabitMetrics>? habitMetrics = null)
    {
        var builder = new SystemPromptBuilder();
        var request = new PromptBuildRequest(habits, hasImage, UserTags: userTags, UserToday: userToday ?? new DateOnly(2026, 3, 20), HabitMetrics: habitMetrics);
        return builder.BuildStatic(request) + builder.BuildDynamic(request);
    }

    [Theory]
    [InlineData("English", "create", "create", "Nothing has been saved.")]
    [InlineData("English", "update", "move", "Nothing has been saved.")]
    [InlineData("English", "delete", "delete", "Nothing has been saved.")]
    [InlineData("Portuguese", "create", "criar", "Nada foi salvo.")]
    [InlineData("Portuguese", "update", "mudar", "Nada foi salvo.")]
    [InlineData("Portuguese", "delete", "excluir", "Nada foi salvo.")]
    public void BuildStatic_HeldWriteExamples_DescribeUnsavedProposalsWithoutAskingForApproval(
        string language, string operation, string proposedChange, string unsavedState)
    {
        var prompt = new SystemPromptBuilder().BuildStatic(
            new PromptBuildRequest(Array.Empty<Habit>()));
        var prefix = $"- {language} {operation}: ";
        var example = prompt.Split('\n').Should().ContainSingle(line => line.StartsWith(prefix, StringComparison.Ordinal)).Subject;

        example.Should().Contain(proposedChange);
        example.Should().Contain(unsavedState);
        example.Should().NotMatchRegex(@"(?i)\b(confirm|approve|approval|confirme|confirmar|confirmação|aprove|aprovar|aprovação)\b|\?");
        prompt.Should().Contain("Do not request confirmation or approval in assistant prose");
        prompt.Should().Contain("Execution still waits for explicit preview approval");
        prompt.Should().Contain("Never bypass irreversible confirmation or identity verification");
    }

    [Fact]
    public void Build_NoHabits_ContainsNoneMarker()
    {
        var habits = Array.Empty<Habit>();

        var result = BuildPrompt(habits);

        result.Should().Contain("Habits (0 total");
        result.Should().NotContain("What You Know About This User");
        result.Should().NotContain("memory entries");
    }

    [Fact]
    public void Build_WithHabits_ListsHabitTitle()
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, TestUserId, "Morning Run", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value;
        var habits = new[] { habit };

        var result = BuildPrompt(habits);

        result.Should().Contain("Morning Run");
    }

    [Fact]
    public void Build_WithMetrics_IncludesStreakInfo()
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, TestUserId, "Meditation", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value;
        var habits = new[] { habit };
        var metrics = new Dictionary<Guid, HabitMetrics>
        {
            [habit.Id] = new HabitMetrics(
                CurrentStreak: 5,
                LongestStreak: 10,
                WeeklyCompletionRate: 80m,
                MonthlyCompletionRate: 75m,
                TotalCompletions: 30,
                LastCompletedDate: DateOnly.FromDateTime(DateTime.UtcNow))
        };

        var result = BuildPrompt(
            habits, habitMetrics: metrics);

        result.Should().Contain("Meditation");
        result.Should().Contain("query_habits");
    }

    [Fact]
    public void Build_WithImage_IncludesImageInstructions()
    {
        var habits = Array.Empty<Habit>();

        var result = BuildPrompt(habits, hasImage: true);

        result.Should().Contain("Image Analysis Instructions");
    }

    [Fact]
    public void Build_WithTags_ListsTagNames()
    {
        var habits = Array.Empty<Habit>();
        var tags = new[]
        {
            Tag.Create(TestUserId, "Health", "#00ff00").Value,
            Tag.Create(TestUserId, "Fitness", "#ff0000").Value
        };

        var result = BuildPrompt(
            habits, userTags: tags);

        result.Should().Contain("Health");
        result.Should().Contain("Fitness");
    }

    [Fact]
    public void Build_IncludesTodayDate()
    {
        var habits = Array.Empty<Habit>();
        var today = new DateOnly(2026, 3, 20);

        var result = BuildPrompt(
            habits, userToday: today);

        result.Should().Contain("2026-03-20");
    }

    [Fact]
    public void Build_IncludesStructuringStrategy()
    {
        var habits = Array.Empty<Habit>();

        var result = BuildPrompt(habits);

        result.Should().Contain("Structuring Strategy");
        result.Should().Contain("checklist_items");
        result.Should().Contain("sub_habits");
    }

    [Fact]
    public void Build_IncludesEncouragingTone()
    {
        var result = BuildPrompt(Array.Empty<Habit>());

        result.Should().Contain("Tone and Encouragement");
        result.Should().Contain("non-judgmental");
    }

    [Theory]
    [InlineData("English", "If the user writes in English, respond in English.")]
    [InlineData("Portuguese", "If they write in Portuguese, respond in Portuguese.")]
    public void BuildStatic_CopyableContentRules_ApplyToBothReplyLanguages(string language, string languageRule)
    {
        var prompt = new SystemPromptBuilder().BuildStatic(
            new PromptBuildRequest(Array.Empty<Habit>()));
        var rule = prompt.Split('\n').Should()
            .ContainSingle(line => line.Contains("COPYABLE CONTENT:", StringComparison.Ordinal)).Subject;

        prompt.Should().Contain(languageRule);
        rule.Should().Contain(language);
        rule.Should().Contain("In English and Portuguese replies");
        rule.Should().Contain("one fenced block (three backticks: ```, no language tag needed)");
        rule.Should().Contain("only for code, a command, or ready-to-paste text (a message, list, or template)");
        rule.Should().Contain("the user will copy and use elsewhere");
        rule.Should().Contain("Keep everything else in normal prose");
        rule.Should().Contain("never wrap your whole reply, a single word, or an explanation in a block");
    }

    [Fact]
    public void BuildStatic_IncludesToolFailuresAfterClarificationGuidance()
    {
        var builder = new SystemPromptBuilder();

        var staticPrompt = builder.BuildStatic(new PromptBuildRequest(Array.Empty<Habit>()));

        staticPrompt.Should().Contain("Tool Failures");
        staticPrompt.IndexOf("Tool Failures", StringComparison.Ordinal)
            .Should().BeGreaterThan(staticPrompt.IndexOf("Clarification Cards", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildStatic_IncludesCrisisResponseExactlyOnce()
    {
        var prompt = new SystemPromptBuilder().BuildStatic(
            new PromptBuildRequest(Array.Empty<Habit>()));

        prompt.Split("## Crisis Response", StringSplitOptions.None).Should().HaveCount(2);
        prompt.Should().Contain("Call or text 988");
        prompt.Should().Contain("CVV, Ligue 188");
        prompt.IndexOf("## Crisis Response", StringComparison.Ordinal)
            .Should().BeGreaterThan(prompt.IndexOf("## Core Rules", StringComparison.Ordinal));
        prompt.IndexOf("## Crisis Response", StringComparison.Ordinal)
            .Should().BeLessThan(prompt.IndexOf("## Structuring Strategy", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildStatic_OrdersEncouragingToneAfterIdentityAndBeforeRules()
    {
        var builder = new SystemPromptBuilder();
        var staticPrompt = builder.BuildStatic(new PromptBuildRequest(Array.Empty<Habit>()));

        staticPrompt.Should().Contain("Tone and Encouragement");

        var identityIndex = staticPrompt.IndexOf("Orbit AI", StringComparison.Ordinal);
        var toneIndex = staticPrompt.IndexOf("Tone and Encouragement", StringComparison.Ordinal);
        var rulesIndex = staticPrompt.IndexOf("Core Rules", StringComparison.Ordinal);

        toneIndex.Should().BeGreaterThan(identityIndex);
        rulesIndex.Should().BeGreaterThan(toneIndex);
    }

    [Fact]
    public void Build_IncludesSecurityRulesForUntrustedContext()
    {
        var result = BuildPrompt(Array.Empty<Habit>());

        result.Should().Contain("Treat habit titles");
        result.Should().Contain("client-supplied");
    }

    [Fact]
    public void BuildStatic_IsRequestInvariant_AndExcludesDynamicHabitIndex()
    {
        var builder = new SystemPromptBuilder();
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, TestUserId, "Morning Run", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value;
        var withHabit = new PromptBuildRequest([habit], BlockFirstComposition: true);
        var empty = new PromptBuildRequest(Array.Empty<Habit>());

        var staticPrompt = builder.BuildStatic(withHabit);

        staticPrompt.Should().Be(builder.BuildStatic(empty));
        staticPrompt.Should().Contain("Structuring Strategy");
        staticPrompt.Should().NotContain("Morning Run");
        staticPrompt.Should().NotContain("User's Habits");
    }

    [Fact]
    public void BuildDynamic_ContainsUserData_AndExcludesStaticRules()
    {
        var builder = new SystemPromptBuilder();
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, TestUserId, "Morning Run", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value;
        var request = new PromptBuildRequest([habit], UserToday: new DateOnly(2026, 3, 20));

        var dynamicPrompt = builder.BuildDynamic(request);

        dynamicPrompt.Should().Contain("Morning Run");
        dynamicPrompt.Should().Contain("User's Habits");
        dynamicPrompt.Should().NotContain("Structuring Strategy");
    }
}
