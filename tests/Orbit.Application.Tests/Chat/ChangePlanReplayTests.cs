using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

/// <summary>
/// The <c>ai_change_plan</c> path (thomasluizon/orbit-tickets#2159): the recorded rounds of the owner's Water turns, the
/// staging cards and run 4 of #2127, with the flag on. The model is the only owner of the act-or-ask decision: no
/// server question, no guard refusal of a correct write, tool results with data and typed errors only, and an
/// <c>ask</c> that needs no read.
/// </summary>
public partial class ProcessUserChatCommandHandlerTests
{
    /// <summary>The model-facing payload form: the provider request drops null fields.</summary>
    private static readonly JsonSerializerOptions ModelPayloadJson = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private void EnableChangePlan(params string[] others) => _featureFlagService
        .GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
        .Returns([FeatureFlagKeys.AiChangePlan, .. others]);

    /// <summary>
    /// No guard refusal, no server-written question and no instruction in a tool result. The correction path stays
    /// until step 6-api, so a turn that corrects a denied write keeps the correction text (<paramref name="correction"/>).
    /// </summary>
    private static void ShouldHoldNoServerQuestion(ChatResponse response, IEnumerable<IReadOnlyList<AiToolCallResult>> rounds,
        bool correction = false)
    {
        var results = rounds.SelectMany(round => round).ToArray();
        results.Should().NotContain(result => result.Error != null
            && (result.Error.Contains("Selection is unanswered", StringComparison.Ordinal)
                || result.Error.Contains("Several targets share a title", StringComparison.Ordinal)));
        var payloads = JsonSerializer.Serialize(results.Select(result => result.Payload), ModelPayloadJson);
        payloads.Should().NotContain("selection_required");
        if (!correction)
            payloads.Should().NotContain("\"instruction\"");
        foreach (var portuguese in new[] { false, true })
            response.AiMessage.Should().NotContain(ErrorCopy.Resolve("chat.answer.selectHabit", portuguese));
        (response.AiMessage ?? string.Empty).Should().NotContain("Which habit would you like to use")
            .And.NotContain("Qual hábito você quer usar");
    }

    [Theory]
    [InlineData("Delete the Water - 710ml habit.", "en")]
    [InlineData("Apaga o hábito Water - 710ml.", "pt-BR")]
    public async Task ChangePlan_SingularWaterWriteRunsWithNoServerQuestion(string message, string language)
    {
        var run = await RunShortRequestAsync(message, language, children => [DeleteCall(children[..1])], changePlan: true);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        ShouldHoldNoServerQuestion(run.Result.Value, run.ToolResults);
        run.Held.Select(request => request.OperationId).Should().Equal(["bulk_delete_habits"], "the reply was: " + run.Result.Value.AiMessage);
        run.Result.Value.PendingOperations.Should().ContainSingle().Which.Items!.Select(item => item.EntityId)
            .Should().Equal(run.Children[0].Id);
    }

    [Theory]
    [InlineData("Delete the Water - 710ml habit.", "en")]
    [InlineData("Apaga o hábito Water - 710ml.", "pt-BR")]
    public async Task OldPath_SingularWaterWriteKeepsTodaysGuard(string message, string language)
    {
        var run = await RunShortRequestAsync(message, language, children => [DeleteCall(children[..1])]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Held.Should().BeEmpty();
        run.ToolResults.SelectMany(round => round).Should().Contain(result => result.Error != null
            && result.Error.Contains("Selection is unanswered", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("delete two of them, and rename the other 4 to Water - 1.18L", "en", false)]
    [InlineData("deleta dois desses, e os outros 4 coloca Water - 1.18L", "pt-BR", false)]
    [InlineData("delete two of them, and rename the other 4 to Water - 1.18L", "en", true)]
    [InlineData("deleta dois desses, e os outros 4 coloca Water - 1.18L", "pt-BR", true)]
    public async Task ChangePlan_SplitWaterRequestReachesBothWritesAndTheReadHoldsRecordsOnly(string message, string language,
        bool forSelection)
    {
        var run = await RunShortRequestAsync(message, language,
            children => [DeleteCall(children[4..]), RenameCall(children[..4])], changePlan: true,
            lookup: forSelection ? """{"search":"Water 710ml","for_selection":true,"orbit_read_complete":false}"""
                : """{"search":"Water 710ml","orbit_read_complete":false}""");

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        ShouldHoldNoServerQuestion(run.Result.Value, run.ToolResults);
        var lookup = run.ToolResults[0].Should().ContainSingle(result => result.Id == "lookup").Subject;
        lookup.Success.Should().BeTrue();
        lookup.Payload.Should().BeOfType<HabitListCard>().Which.Selection.Should().BeNull();
        lookup.ReadDirective.Should().NotBeNull();
        run.Held.Select(request => request.OperationId).Should().BeEquivalentTo(["bulk_delete_habits", "bulk_update_habits"],
            "the reply was: " + run.Result.Value.AiMessage);
        run.Result.Value.Parts?.OfType<ChatBlockPart>().Should().BeEmpty();
    }

    [Theory]
    [InlineData("Delete all my Water - 710ml habits.", "en")]
    [InlineData("Apaga todos os meus hábitos Water - 710ml.", "pt-BR")]
    public async Task ChangePlan_FailedAskIsATypedErrorAndTheWriteStillRuns(string message, string language)
    {
        var ask = new AiToolCall("ask", "ask", ParseArguments(
            """{"missing_input":"record_selection","form":"choice","question":"Which?","options":[{"id":"a","label":"First"},{"id":"b","label":"Second"}]}"""));
        var run = await RunShortRequestAsync(message, language, children => [DeleteCall(children[..1])],
            firstRound: [ask], parts: false, changePlan: true);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        var failed = run.ToolResults[0].Should().ContainSingle(result => result.Id == "ask").Subject;
        failed.Success.Should().BeFalse();
        failed.Error.Should().Be("ask_unavailable");
        JsonSerializer.Serialize(failed.Payload).Should().Be("""{"error":"ask_unavailable"}""");
        ShouldHoldNoServerQuestion(run.Result.Value, run.ToolResults);
        run.Held.Select(request => request.OperationId).Should().Equal("bulk_delete_habits");
    }

    [Theory]
    [InlineData(1, "en")]
    [InlineData(2, "en")]
    [InlineData(3, "en")]
    [InlineData(4, "en")]
    [InlineData(5, "en")]
    [InlineData(1, "pt-BR")]
    [InlineData(2, "pt-BR")]
    [InlineData(3, "pt-BR")]
    [InlineData(4, "pt-BR")]
    [InlineData(5, "pt-BR")]
    public async Task ChangePlan_StagingCardShowsAsTheModelWroteItWithNoServerQuestion(int captureRun, string language)
    {
        EnableChangePlan();
        var run = await RunSelectionCardAsync(language, ["habitList", "choice", "question"],
            children => RecordedCard(captureRun, children, language == "pt-BR"));

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        ShouldHoldNoServerQuestion(run.Result.Value, run.ToolResults);
        var issued = run.Issued.Should().ContainSingle().Subject.ReadDefinition();
        var recorded = JsonSerializer.SerializeToElement(RecordedCard(captureRun, run.Children, language == "pt-BR"));
        issued.Form.Should().Be(AiBlockKinds.Choice);
        issued.Title.Should().Be(recorded.GetProperty("question").GetString());
        issued.Options.Select(option => option.Id).Should().Equal(recorded.GetProperty("options").EnumerateArray()
            .Select(option => option.GetProperty("id").GetString()));
        run.ToolResults.SelectMany(round => round).Where(result => result.Name == "ask").Should().OnlyContain(result => result.Success);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task ChangePlan_Run4AnswerTurnKeepsTheCorrectionAndReachesOnePreviewWithBothActions(string language)
    {
        var run = await RunDeniedAnswerTurnAsync(language, children => [Calls(CorrectedRename(children)), Text(language)],
            arrange: _ => EnableChangePlan(FeatureFlagKeys.AiBlockFirst, FeatureFlagKeys.AiConversationRecording));

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        ShouldHoldNoServerQuestion(run.Result.Value, run.ToolResults, correction: true);
        var items = run.Result.Value.PendingOperations.Should().NotBeNullOrEmpty().And.Subject
            .SelectMany(operation => operation.Items!).ToArray();
        items.Where(item => item.Fields.Any(field => field.Field == "delete")).Select(item => item.EntityId)
            .Should().BeEquivalentTo(run.Children[4..].Select(child => child.Id));
        items.Where(item => item.Fields.Any(field => field.Field == "title")).Select(item => item.EntityId)
            .Should().BeEquivalentTo(run.Children[..4].Select(child => child.Id));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task ChangePlan_Run4FailedCorrectionLeavesNoPreviewAndSaysSo(string language)
    {
        var run = await RunDeniedAnswerTurnAsync(language, _ => [Text(language), Text(language)],
            arrange: _ => EnableChangePlan(FeatureFlagKeys.AiBlockFirst, FeatureFlagKeys.AiConversationRecording));

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty("the rename half of the request was not prepared");
        run.Result.Value.AiMessage.Should().Be(NotPreparedFor(language));
    }

    /// <summary>
    /// The run-5 capture of #2127: the model read the records for a selection, and the reply held only a list card.
    /// On the flag the read holds the records and no rule, and the next round's split reaches both writes.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task ChangePlan_Run5SelectionReadLeadsToBothWrites(string language)
    {
        var run = await RunShortRequestAsync(language == "en" ? OwnerSplitMessageEn : OwnerSplitMessagePt, language,
            children => [DeleteCall(children[4..]), RenameCall(children[..4])], changePlan: true,
            lookup: """{"search":"Water 710ml","for_selection":true,"orbit_read_complete":false}""");

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        ShouldHoldNoServerQuestion(run.Result.Value, run.ToolResults);
        run.Held.Select(request => request.OperationId).Should().BeEquivalentTo(["bulk_delete_habits", "bulk_update_habits"]);
    }

    [Theory]
    [InlineData("pt-BR", "Deixe eu escolher quais dos meus hábitos concluir, sem agir ainda.", true)]
    [InlineData("en", "Let me pick which of my habits to complete, without acting yet.", true)]
    [InlineData("pt-BR", "Deixe eu escolher quais dos meus hábitos concluir, sem agir ainda.", false)]
    [InlineData("en", "Let me pick which of my habits to complete, without acting yet.", false)]
    public async Task ChangePlan_SelectionReadAloneIssuesNoServerPickList(string language, string message, bool changePlan)
    {
        var habits = SetupPickListTurn(language);
        if (changePlan)
            EnableChangePlan();
        var toolResults = new List<IReadOnlyList<AiToolCallResult>>();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Do<IReadOnlyList<AiToolCallResult>>(results => toolResults.Add([.. results])),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ConversationContext = TestConversationContext, ToolCalls = [ModelPickList(habits, "log_habits")] }));

        var result = await PickListTurnAsync(message, language);

        result.IsSuccess.Should().BeTrue(result.Error);
        if (changePlan)
        {
            var read = toolResults.Should().ContainSingle("the model reads the records and writes its own question").Subject
                .Should().ContainSingle().Subject;
            read.Payload.Should().BeOfType<HabitListCard>().Which.Selection.Should().BeNull();
            JsonSerializer.Serialize(read.Payload, ModelPayloadJson).Should().NotContain("selection_required").And.NotContain("instruction");
        }
        else
        {
            toolResults.Should().BeEmpty("the server issues its own pick list without a second model round");
        }
        result.Value.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Which.Block.Should().BeOfType<AiPickListBlock>();
    }

    private sealed record AboutAskRun(Result<ChatResponse> Result, Habit[] Children, Habit Stranger, List<IssuedAiBlock> Issued,
        List<IReadOnlyList<AiToolCallResult>> ToolResults);

    /// <summary>
    /// One turn whose first and only call is a choice about records, with no read. The children of a parent "Water"
    /// share the title "Water - 710ml" and carry positions in the reverse order of their creation, so the app list
    /// order differs from the creation order and from the order of <c>about</c>.
    /// </summary>
    private async Task<AboutAskRun> RunAboutAskAsync(int count, string language, string[] partKinds,
        Func<Habit[], Habit, IEnumerable<object>> about, bool changePlan = true, bool several = false)
    {
        if (changePlan)
            EnableChangePlan();
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        var parent = CreateHabit("Water");
        var children = Enumerable.Range(0, count).Select(_ => CreateHabit("Water - 710ml")).ToArray();
        for (var index = 0; index < children.Length; index++)
        {
            children[index].SetParentHabitId(parent.Id);
            children[index].SetPosition(children.Length - index);
        }
        var stranger = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, Guid.NewGuid(), "Private stranger habit",
            null, null, DueDate: Today)).Value;
        SetupSelectionHabits([parent, .. children, stranger]);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(query, RealAskTool());
        HoldSelectionReplayWrites(query);
        var issued = new List<IssuedAiBlock>();
        _blockStore.AddAsync(Arg.Do<IssuedAiBlock>(issued.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var question = language == "en" ? "Which Water - 710ml do you mean?" : "Qual Water - 710ml você quer?";
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = [new("ask", "ask", JsonSerializer.SerializeToElement(new
            {
                input = new
                {
                    form = "choice",
                    missing_input = "record_selection",
                    question,
                    about = about(children, stranger).Select(item => item is Habit habit ? habit.Id.ToString() : item.ToString()),
                    several,
                    allowFreeText = false
                }
            }))]
        });
        var toolResults = new List<IReadOnlyList<AiToolCallResult>>();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Do<IReadOnlyList<AiToolCallResult>>(results => toolResults.Add([.. results])),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = language == "en" ? "Tell me which one." : "Diga qual." }));
        var result = await handler.Handle(new(UserId, language == "en" ? "Rename my Water - 710ml habit to Water - 1L" : "Renomeia meu hábito Water - 710ml para Water - 1L",
            ClientContext: new(SupportsParts: true, PartKinds: partKinds, Locale: language)), default);
        return new AboutAskRun(result, children, stranger, issued, toolResults);
    }

    private static IEnumerable<Habit> Shuffled(Habit[] children) =>
        children.Where((_, index) => index % 2 == 1).Concat(children.Where((_, index) => index % 2 == 0));

    [Theory]
    [InlineData(2, "en")]
    [InlineData(4, "en")]
    [InlineData(6, "en")]
    [InlineData(10, "en")]
    [InlineData(2, "pt-BR")]
    [InlineData(4, "pt-BR")]
    [InlineData(6, "pt-BR")]
    [InlineData(10, "pt-BR")]
    public async Task ChangePlan_AskAboutRecordsBuildsOneOptionForEachRecordInTheAppListOrder(int count, string language)
    {
        var run = await RunAboutAskAsync(count, language, ["choice", "question"], (children, _) => Shuffled(children));

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        var definition = run.Issued.Should().ContainSingle().Subject.ReadDefinition();
        var appOrder = run.Children.Reverse().ToArray();
        var place = (int index) => language == "en" ? $"{index + 1} of {count}" : $"{index + 1}º de {count}";
        if (count <= 4)
        {
            definition.Form.Should().Be(AiBlockKinds.Choice);
            definition.Options.Select(option => option.Id).Should().Equal(appOrder.Select(child => child.Id.ToString()));
            definition.Options.Select(option => option.Label).Should().AllBe("Water - 710ml");
            definition.Options.Select(option => option.Description).Should().Equal(appOrder.Select((_, index) => $"Water, {place(index)}"));
            definition.MultiSelect.Should().BeFalse();
        }
        else
        {
            definition.Form.Should().Be(AiBlockKinds.Question);
            var question = definition.Questions.Should().ContainSingle().Subject;
            question.Type.Should().Be("singleChoice");
            question.Options!.Select(option => option.Id).Should().Equal(appOrder.Select(child => child.Id.ToString()));
            question.Options!.Select(option => option.Label).Should().Equal(appOrder.Select((_, index) => $"Water - 710ml (Water, {place(index)})"));
        }
        run.Result.Value.Parts!.OfType<ChatBlockPart>().Should().ContainSingle();
        run.ToolResults.Should().BeEmpty("a valid ask ends the reply");
    }

    [Theory]
    [InlineData(11, new[] { "choice", "question" }, 10, "en")]
    [InlineData(11, new[] { "choice", "question" }, 10, "pt-BR")]
    [InlineData(5, new[] { "choice" }, 4, "en")]
    [InlineData(5, new[] { "choice" }, 4, "pt-BR")]
    public async Task ChangePlan_AskAboutTooManyRecordsReturnsTooManyCandidates(int count, string[] partKinds, int limit, string language)
    {
        var run = await RunAboutAskAsync(count, language, partKinds, (children, _) => children);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Issued.Should().BeEmpty();
        var failed = run.ToolResults.Should().ContainSingle().Which.Should().ContainSingle().Subject;
        failed.Success.Should().BeFalse();
        failed.Error.Should().Be("too_many_candidates");
        var payload = JsonSerializer.SerializeToElement(failed.Payload);
        payload.GetProperty("error").GetString().Should().Be("too_many_candidates");
        payload.GetProperty("count").GetInt32().Should().Be(count);
        payload.GetProperty("limit").GetInt32().Should().Be(limit);
        payload.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("error", "count", "limit");
    }

    [Theory]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    public async Task ChangePlan_AskAboutARecordOfAnotherPersonReturnsATypedErrorWithNoDataOfThatPerson(string language,
        bool strangerRecord)
    {
        var run = await RunAboutAskAsync(3, language, ["choice", "question"],
            (children, stranger) => [children[0], children[1], strangerRecord ? stranger : Guid.NewGuid()]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Issued.Should().BeEmpty();
        var failed = run.ToolResults.Should().ContainSingle().Which.Should().ContainSingle().Subject;
        failed.Error.Should().Be("unknown_record");
        JsonSerializer.Serialize(failed.Payload).Should().Be("""{"error":"unknown_record","count":1}""");
        JsonSerializer.Serialize(failed).Should().NotContain("Private stranger habit")
            .And.NotContain(run.Stranger.Id.ToString());
    }

    [Theory]
    [InlineData(true, "en")]
    [InlineData(false, "en")]
    [InlineData(true, "pt-BR")]
    [InlineData(false, "pt-BR")]
    public async Task AskAsTheFirstCallWithNoRead_ShowsACardOnlyOnTheFlag(bool changePlan, string language)
    {
        EnableComposition();
        if (changePlan)
            EnableChangePlan(FeatureFlagKeys.AiBlockFirst, FeatureFlagKeys.AiConversationRecording);
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        var handler = CreateHandler(RealAskTool());
        const string choice = """{"input":{"missing_input":"preference","form":"choice","question":"When?","options":[{"id":"a","label":"Morning"},{"id":"b","label":"Evening"}]}}""";
        SetupAiResponse(new AiResponse { ConversationContext = TestConversationContext, ToolCalls = [new("ask", "ask", ParseArguments(choice))] });
        var toolResults = new List<IReadOnlyList<AiToolCallResult>>();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Do<IReadOnlyList<AiToolCallResult>>(results => toolResults.Add([.. results])),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = language == "en" ? "When do you prefer?" : "Quando você prefere?" }));

        var result = await handler.Handle(new(UserId, language == "en" ? "Help me plan a walk" : "Me ajuda a planejar uma caminhada",
            ClientContext: new(SupportsParts: true, PartKinds: ["choice"], Locale: language)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        if (changePlan)
        {
            result.Value.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Which.Kind.Should().Be("choice");
            toolResults.Should().BeEmpty();
        }
        else
        {
            result.Value.Parts?.OfType<ChatBlockPart>().Should().BeEmpty();
            toolResults.Should().ContainSingle().Which.Should().ContainSingle()
                .Which.Error.Should().StartWith(AskTool.AccountReadInstruction);
        }
    }
}
