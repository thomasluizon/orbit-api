using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

/// <summary>
/// The owner's short requests over six same-titled sub-habits (thomasluizon/orbit-tickets#2129). Each turn runs the
/// production read tool and the real selection guard, with the model rounds recorded as replies.
/// </summary>
public partial class ProcessUserChatCommandHandlerTests
{
    private const string SplitRule = "Writes in one round that make different changes and together cover every candidate with that title exactly once are accepted.";

    [Theory]
    [InlineData("Delete all my Water - 710ml habits.", "en", false)]
    [InlineData("Apaga todos os meus hábitos Water - 710ml.", "pt-BR", false)]
    [InlineData("Rename the last 2 Water - 710ml habits to Water - 1L.", "en", true)]
    [InlineData("Renomeia os 2 últimos Water - 710ml para Water - 1L.", "pt-BR", true)]
    public async Task ShortRequest_SetRequestReadWithoutPurposeAsksNothingAndReachesOnePreview(string message, string language, bool rename)
    {
        var run = await RunShortRequestAsync(message, language, children => rename
            ? [UpdateCall("rename", children[4..], new { title = "Water - 1L" })]
            : [DeleteCall(children)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        var lookup = run.ToolResults[0].Should().ContainSingle(result => result.Id == "lookup").Subject;
        var payload = JsonSerializer.Serialize(lookup.Payload);
        payload.Should().NotContain("selection_required").And.NotContain("instruction").And.NotContain("Ask the person");
        lookup.ReadDirective.Should().NotBeNull();
        run.Held.Select(request => request.OperationId).Should().Equal([rename ? "bulk_update_habits" : "bulk_delete_habits"],
            "the reply was: " + run.Result.Value.AiMessage);
        run.Result.Value.PendingOperations.Should().ContainSingle().Which.Items!.Select(item => item.EntityId)
            .Should().BeEquivalentTo((rename ? run.Children[4..] : run.Children).Select(child => child.Id));
        run.Result.Value.Parts?.OfType<ChatBlockPart>().Should().BeEmpty();
        run.Result.Value.AiMessage.Should().NotContain("Which habit").And.NotContain("Qual hábito");
    }

    [Theory]
    [InlineData("delete two of them, and rename the other 4 to Water - 1.18L", "en")]
    [InlineData("deleta dois desses, e os outros 4 coloca Water - 1.18L", "pt-BR")]
    public async Task ShortRequest_UnsettledReadReportsTheGuardRuleAndTheSplitReachesOnePreview(string message, string language)
    {
        var run = await RunShortRequestAsync(message, language,
            children => [DeleteCall(children[4..]), RenameCall(children[..4])]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        var lookup = run.ToolResults[0].Should().ContainSingle(result => result.Id == "lookup").Subject;
        var instruction = JsonSerializer.SerializeToElement(lookup.Payload).GetProperty("instruction").GetString();
        instruction.Should().Contain(SplitRule).And.NotContain("prepare any write");
        run.Held.Select(request => request.OperationId).Should().BeEquivalentTo(["bulk_delete_habits", "bulk_update_habits"],
            "the reply was: " + run.Result.Value.AiMessage);
        run.Result.Value.PendingOperations.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("Delete all my Water - 710ml habits.", "en", false)]
    [InlineData("Apaga todos os meus hábitos Water - 710ml.", "pt-BR", false)]
    [InlineData("Delete the Water - 710ml habit.", "en", true)]
    [InlineData("Apaga o hábito Water - 710ml.", "pt-BR", true)]
    public async Task ShortRequest_FailedAskTellsThePlainTextQuestionOnlyWhenASelectionWaits(string message, string language, bool waits)
    {
        var ask = new AiToolCall("ask", "ask", ParseArguments(
            """{"missing_input":"record_selection","form":"choice","question":"Which?","options":[{"id":"a","label":"First"},{"id":"b","label":"Second"}]}"""));
        var run = await RunShortRequestAsync(message, language, children => [DeleteCall(waits ? children[..1] : children)],
            firstRound: [ask], parts: false);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        var failed = run.ToolResults[0].Should().ContainSingle(result => result.Id == "ask").Subject;
        failed.Success.Should().BeFalse();
        if (waits)
            failed.Error.Should().EndWith(" Ask in plain text instead.");
        else
            failed.Error.Should().NotContain("Ask in plain text");
        run.Held.Select(request => request.OperationId).Should().Equal(waits ? [] : ["bulk_delete_habits"],
            "the reply was: " + run.Result.Value.AiMessage);
    }

    [Theory]
    [InlineData("Delete the Water - 710ml habit.", "en")]
    [InlineData("Apaga o hábito Water - 710ml.", "pt-BR")]
    public async Task ShortRequest_RoundLogNamesEachFailedToolAndItsErrorKey(string message, string language)
    {
        _logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var ask = new AiToolCall("ask", "ask", ParseArguments(
            """{"missing_input":"record_selection","form":"choice","question":"Which?","options":[{"id":"a","label":"First"},{"id":"b","label":"Second"}]}"""));
        var run = await RunShortRequestAsync(message, language, children => [DeleteCall(children[..1])],
            firstRound: [ask], parts: false);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        var failedCalls = RoundLogs().Select(state => state.GetValueOrDefault("FailedCalls")?.ToString()).ToArray();
        failedCalls.Should().Equal("ask:ask_unavailable", "bulk_delete_habits:selection_unanswered");
        JsonSerializer.Serialize(RoundLogs()).Should().NotContain("Water").And.NotContain(message);
    }

    [Theory]
    [InlineData("Rename one Walk to Morning walk and the other to Evening walk.", "en")]
    [InlineData("Renomeia um Walk para Morning walk e o outro para Evening walk.", "pt-BR")]
    public async Task ShortRequest_AWriteThatAnotherSelectionHoldsCoversNoPartOfASplit(string message, string language)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        var walks = new[] { CreateHabit("Walk"), CreateHabit("Walk") };
        var runs = new[] { CreateHabit("Run"), CreateHabit("Run fast") };
        SetupSelectionHabits([.. walks, .. runs]);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var update = new BulkUpdateHabitsTool(_mediator);
        var handler = CreateHandler(query, update);
        var held = HoldSelectionReplayWrites(query, update);
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls =
            [
                new(query.Name, "walks", ParseArguments("""{"search":"Walk","for_selection":false,"orbit_read_complete":false}""")),
                new(query.Name, "runs", ParseArguments("""{"search":"Run","for_selection":false,"orbit_read_complete":false}""")),
                UpdateCall("first", [walks[0], runs[0]], new { title = "Morning walk" }),
                UpdateCall("second", walks[1..], new { title = "Evening walk" })
            ]
        });
        ReplyToReplay("Review the changes.");

        var result = await handler.Handle(new(UserId, message,
            ClientContext: new(SupportsParts: true, PartKinds: ["habitList", "choice"], SupportsHabitListCard: true,
                SupportsDatedHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        held.Should().BeEmpty("the first write is held by the Run selection, so the second write covers half of the Walk group");
        result.Value.PendingOperations.Should().BeNullOrEmpty();
    }

    private IEnumerable<Dictionary<string, object?>> RoundLogs() => _logger.ReceivedCalls()
        .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log))
        .Select(call => (IEnumerable<KeyValuePair<string, object?>>)call.GetArguments()[2]!)
        .Where(state => state.Any(pair => pair.Key == "FailedCount" && pair.Value is > 0))
        .Select(state => state.ToDictionary(pair => pair.Key, pair => pair.Value));

    private sealed record ShortRequestRun(Result<ChatResponse> Result, List<AgentExecuteOperationRequest> Held, Habit[] Children,
        List<IReadOnlyList<AiToolCallResult>> ToolResults);

    /// <summary>
    /// One turn over a parent "Water" with six "Water - 710ml" sub-habits. The first round reads the habits with a
    /// name search and no for_selection argument, as the owner's turns did, beside the optional first-round calls.
    /// The second round sends the writes, and the third round ends the turn with text.
    /// </summary>
    private async Task<ShortRequestRun> RunShortRequestAsync(string message, string language, Func<Habit[], AiToolCall[]> writes,
        AiToolCall[]? firstRound = null, bool parts = true)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        var parent = CreateHabit("Water");
        var children = Enumerable.Range(0, 6).Select(_ => CreateHabit("Water - 710ml")).ToArray();
        foreach (var child in children) child.SetParentHabitId(parent.Id);
        SetupSelectionHabits([parent, .. children]);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var delete = new BulkDeleteHabitsTool(_mediator, _habitRepo);
        var update = new BulkUpdateHabitsTool(_mediator);
        var handler = CreateHandler(query, delete, update, RealAskTool());
        var held = HoldSelectionReplayWrites(query, delete, update);
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = [new(query.Name, "lookup", ParseArguments("""{"search":"Water 710ml","orbit_read_complete":false}""")),
                .. firstRound ?? []]
        });
        var toolResults = new List<IReadOnlyList<AiToolCallResult>>();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Do<IReadOnlyList<AiToolCallResult>>(results => toolResults.Add([.. results])),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ConversationContext = TestConversationContext, ToolCalls = writes(children) }),
                Result.Success(new AiResponse { TextMessage = language == "en" ? "Review the changes." : "Revise as mudanças." }));
        var result = await handler.Handle(new ProcessUserChatCommand(UserId, message,
            ClientContext: new(SupportsParts: parts, PartKinds: parts ? ["habitList", "choice"] : null,
                SupportsHabitListCard: true, SupportsDatedHabitListCard: true)), default);
        return new ShortRequestRun(result, held, children, toolResults);
    }
}
