using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;
using Xunit;

namespace Orbit.Application.Tests.Chat;

/// <summary>
/// The path of a turn is one object that the handler resolves once. These tests drive each path through that seam
/// (thomasluizon/orbit-tickets#2159).
/// </summary>
public class TurnPolicyTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static Habit CreateHabit() => Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, UserId, "Water", null, null,
        DueDate: new DateOnly(2026, 11, 18))).Value;

    private static AiToolCall BulkDelete(string id, params Habit[] habits) => new("bulk_delete_habits", id,
        JsonSerializer.SerializeToElement(new { habit_ids = habits.Select(habit => habit.Id) }));

    private static HabitListCardItem Item(Habit habit) => new(habit.Id.ToString(), habit.Title, null, 0, false, "general");

    [Fact]
    public void For_ResolvesThePathFromTheFlagOnce()
    {
        ProcessUserChatCommandHandler.TurnPolicy.For([]).Should().BeSameAs(ProcessUserChatCommandHandler.TurnPolicy.Legacy);
        ProcessUserChatCommandHandler.TurnPolicy.For([FeatureFlagKeys.AiChangePlan]).ChangePlan.Should().BeTrue();
        ProcessUserChatCommandHandler.TurnPolicy.For(["other"]).ChangePlan.Should().BeFalse();
    }

    /// <summary>
    /// Two writes share a same-title group of three records and the turn read two of them. The delete targets an unread
    /// record, so the duplicate guard refuses it. The old path builds the scope of the turn after that refusal, as
    /// <c>main</c> does, so the refused write is no part of the scope.
    /// </summary>
    [Fact]
    public void LegacyPolicy_BuildsTheSelectionScopeAfterTheDuplicateGuardRefusedAWrite()
    {
        var habits = Enumerable.Range(0, 3).Select(_ => CreateHabit()).ToArray();
        var refused = BulkDelete("refused", habits[1], habits[2]);
        var partner = BulkDelete("partner", habits[0]);
        var results = new ProcessUserChatCommandHandler.ToolExecutionAccumulator { AllHabits = habits };
        results.ReadHabitItems[habits[0].Id.ToString()] = Item(habits[0]);
        results.ReadHabitItems[habits[1].Id.ToString()] = Item(habits[1]);
        var resolved = new Dictionary<string, AiToolCall?> { [refused.Id] = refused, [partner.Id] = partner };
        var outcomes = new Dictionary<string, ProcessUserChatCommandHandler.ToolCallOutcome>();

        var scope = results.Policy.RejectHeldSelections(new(UserId, "delete two of the Water habits"),
            [refused, partner], resolved, results, outcomes);

        results.Policy.Should().BeSameAs(ProcessUserChatCommandHandler.TurnPolicy.Legacy);
        outcomes[refused.Id].FailureKey.Should().Be("unread_duplicate_targets");
        resolved[refused.Id].Should().BeNull();
        scope.TurnWrites.Select(write => write.Id).Should().Equal(partner.Id);
    }

    [Fact]
    public void ChangePlanPolicy_RunsNoDuplicateGuardAndKeepsEveryWriteInTheScope()
    {
        var habits = Enumerable.Range(0, 3).Select(_ => CreateHabit()).ToArray();
        var first = BulkDelete("first", habits[1], habits[2]);
        var second = BulkDelete("second", habits[0]);
        var results = new ProcessUserChatCommandHandler.ToolExecutionAccumulator
        {
            AllHabits = habits,
            Policy = ProcessUserChatCommandHandler.TurnPolicy.For([FeatureFlagKeys.AiChangePlan])
        };
        var resolved = new Dictionary<string, AiToolCall?> { [first.Id] = first, [second.Id] = second };
        var outcomes = new Dictionary<string, ProcessUserChatCommandHandler.ToolCallOutcome>();

        var scope = results.Policy.RejectHeldSelections(new(UserId, "delete two of the Water habits"),
            [first, second], resolved, results, outcomes);

        outcomes.Should().BeEmpty();
        resolved.Values.Should().OnlyContain(call => call != null);
        scope.TurnWrites.Select(write => write.Id).Should().BeEquivalentTo([first.Id, second.Id]);
    }

    [Fact]
    public void ResultBoundary_SendsInstructionTextOnTheOldPathAndATypedErrorOnTheFlagPath()
    {
        var call = new AiToolCall("query_habits", "call-1", JsonSerializer.SerializeToElement(new { }));
        var legacy = ProcessUserChatCommandHandler.TurnPolicy.Legacy;
        var plan = ProcessUserChatCommandHandler.TurnPolicy.For([FeatureFlagKeys.AiChangePlan]);
        var details = new Dictionary<string, object?> { ["limit"] = 3 };

        var old = legacy.FailedOutcome(call, "read_call_limit", "Narrow the request.", null, details);
        var typed = plan.FailedOutcome(call, "read_call_limit", "Narrow the request.", null, details);

        old.ToolResult.Error.Should().Be("Narrow the request.");
        old.ToolResult.Payload.Should().BeNull();
        old.FailureKey.Should().Be("read_call_limit");
        typed.ToolResult.Error.Should().Be("read_call_limit");
        JsonSerializer.Serialize(typed.ToolResult.Payload).Should().Be("""{"error":"read_call_limit","limit":3}""");
        typed.FailureKey.Should().Be("read_call_limit");
        legacy.Instruction("Do it.").Should().Be("Do it.");
        plan.Instruction("Do it.").Should().BeNull();
    }

    /// <summary>
    /// The two policy classes are the one place where the paths differ. A handler file that reads the marker to pick a
    /// path brings back a fork outside the policy. The two request markers of <c>Ai.cs</c> are the only readers.
    /// </summary>
    [Fact]
    public void HandlerFiles_DoNotReadTheMarkerToPickAPath()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Orbit.slnx")))
            root = root.Parent;
        root.Should().NotBeNull();
        var directory = Path.Combine(root!.FullName, "src", "Orbit.Application", "Chat", "Commands");
        var forks = Directory.EnumerateFiles(directory, "ProcessUserChatCommand*.cs")
            .Where(path => !path.EndsWith("ProcessUserChatCommand.TurnPolicy.cs", StringComparison.Ordinal))
            .SelectMany(path => File.ReadAllLines(path).Select((line, index) => (File: Path.GetFileName(path), Line: index + 1, Text: line.Trim())))
            .Where(read => System.Text.RegularExpressions.Regex.IsMatch(read.Text, @"\.ChangePlan\b")
                && !System.Text.RegularExpressions.Regex.IsMatch(read.Text, @"^ChangePlan(:| =) context\.Policy\.ChangePlan\)?;?$"))
            .Select(read => $"{read.File}:{read.Line} {read.Text}")
            .ToArray();

        forks.Should().BeEmpty();
    }

    [Fact]
    public void PathMembers_DifferByPolicyOnly()
    {
        var legacy = ProcessUserChatCommandHandler.TurnPolicy.Legacy;
        var plan = ProcessUserChatCommandHandler.TurnPolicy.For([FeatureFlagKeys.AiChangePlan]);

        legacy.DefersCreationBesideRead.Should().BeTrue();
        plan.DefersCreationBesideRead.Should().BeFalse();
        legacy.WithTerminalDirective("system").Should().EndWith(TerminalWriteDirective.Instruction).And.StartWith("system");
        plan.WithTerminalDirective("system").Should().Be("system");
    }
}
