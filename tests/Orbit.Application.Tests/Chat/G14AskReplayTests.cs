using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    public static IEnumerable<object[]> G14UnboundQuestions() =>
        from capture in new[] { "composition-proactive-pt-BR-r2", "composition-restart-approve-pt-BR-r2",
            "composition-coaching-advice-pt-BR-r1", "composition-coaching-advice-pt-BR-r2", "composition-choice-pt-BR-r2" }
        from language in new[] { "en", "pt-BR" }
        select new object[] { capture, language };

    [Theory]
    [MemberData(nameof(G14UnboundQuestions))]
    public async Task G14_CapturedHabitSetupWithoutDraftReturnsFeedbackAndContinues(string capture, string language)
    {
        SetupExistingParents(language);
        EnableComposition();
        SetupAiResponse(CapturedCompositionPlan("prod-r6-" + capture));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = language == "en" ? "Start with one easy action." : "Comece com uma ação fácil." }));
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["choice", "question"], Locale: language);

        var result = await CreateHandler(RealAskTool()).Handle(new(UserId, "Continue.", ClientContext: client), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Parts!.OfType<ChatBlockPart>().Should().BeEmpty();
        await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Count == 1 && !results[0].Success
                && results[0].Error!.Contains("proposed_habit_title")), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        AssertStoredComposition(result.Value, client, language);
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    public async Task G14_CompletedReadValidatesSameResponseAskInEitherOrder(string language, bool askFirst)
    {
        SetupExistingParents(language);
        EnableComposition();
        var first = CreateHabit("Caminhada");
        var second = CreateHabit("Caminhada");
        SetupSelectionHabits(first, second);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(RealAskTool(), query);
        UseProductionReadExecutor(query);
        var captured = CapturedCompositionPlan("prod-r6-ambiguity-pt-r3");
        var args = JsonNode.Parse(captured.ToolCalls![1].Args.GetRawText())!.AsObject();
        args["input"]!["options"]![0]!["id"] = first.Id.ToString();
        args["input"]!["options"]![1]!["id"] = second.Id.ToString();
        var ask = captured.ToolCalls[1] with { Args = JsonSerializer.SerializeToElement(args) };
        SetupAiResponse(captured with { ToolCalls = askFirst ? [ask, captured.ToolCalls[0]] : [captured.ToolCalls[0], ask] });
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["habitList", "choice"], SupportsHabitListCard: true, Locale: language);

        var result = await handler.Handle(new(UserId, "Continue.", ClientContext: client), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var block = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<AstraChoiceBlock>().Subject;
        block.Options.Select(option => option.Id).Should().Equal(first.Id.ToString(), second.Id.ToString());
        block.Options.Select(option => option.Description).Should().OnlyContain(detail => detail ==
            (language == "en" ? "Same details" : "Mesmos dados"));
        await AssertNoCompositionContinuation();
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        AssertStoredComposition(result.Value, client, language);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task G14_CapturedBulkWriteOverDuplicateTitlesRequiresFreshResolution(string language)
    {
        SetupExistingParents(language);
        EnableComposition();
        var first = CreateHabit("Caminhada");
        var second = CreateHabit("Caminhada");
        SetupSelectionHabits(first, second);
        var write = new BulkLogHabitsTool(_mediator, _habitRepo, _userDateService,
            new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(),
                Substitute.For<IIdempotencyStore>(), _unitOfWork));
        var handler = CreateHandler(write);
        UseProductionReadExecutor(write);
        var captured = CapturedCompositionPlan("prod-r6-ambiguity-pt-r1");
        var args = JsonNode.Parse(captured.ToolCalls!.Single().Args.GetRawText())!.AsObject();
        args["habit_ids"] = new JsonArray(first.Id.ToString(), second.Id.ToString());
        SetupAiResponse(captured with { ToolCalls = [captured.ToolCalls![0] with { Args = JsonSerializer.SerializeToElement(args) }] });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = language == "en" ? "Which habit?" : "Qual hábito?" }));

        var result = await handler.Handle(new(UserId, "Continue.", ClientContext: new(SupportsParts: true,
            PartKinds: ["choice"], Locale: language)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Single().Error!.Contains("query_habits")),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void G14_InitialAskSchemaRequiresConcreteNewHabitDraft()
    {
        var forms = JsonSerializer.SerializeToElement(RealAskTool().GetParameterSchema(false))
            .GetProperty("properties").GetProperty("input").GetProperty("anyOf");
        foreach (var form in forms.EnumerateArray())
        {
            form.GetProperty("required").EnumerateArray().Select(field => field.GetString()).Should().Contain("proposed_habit_title");
            form.GetProperty("properties").GetProperty("proposed_habit_title").GetProperty("pattern").GetString().Should().Be(@"\S");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Walk\nRun")]
    public async Task G14_HabitSetupDraftMustHaveAConcreteSingleLineTitle(string? title)
    {
        var arguments = JsonNode.Parse(ChoiceArguments)!.AsObject();
        arguments["missing_input"] = "habit_setup";
        arguments["proposed_habit_title"] = title;

        var result = await RealAskTool().ExecuteAsync(JsonSerializer.SerializeToElement(arguments), UserId, default);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("proposed_habit_title");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task G14_CapturedLengthRetryUsesRefreshedPostReadDeclaration(string language)
    {
        SetupExistingParents(language);
        EnableComposition();
        var habits = new[] { CreateHabit("Caminhada"), CreateHabit("Caminhada") };
        SetupSelectionHabits(habits);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(RealAskTool(), query);
        UseProductionReadExecutor(query);
        var captured = CapturedCompositionPlan("prod-r6-ambiguity-pt-r2");
        var calls = captured.ToolCalls!.Select(call =>
        {
            if (call.Name != "ask") return call;
            var args = JsonNode.Parse(call.Args.GetRawText())!.AsObject();
            for (var index = 0; index < habits.Length; index++)
                args["input"]!["options"]![index]!["id"] = habits[index].Id.ToString();
            return call with { Args = JsonSerializer.SerializeToElement(args) };
        }).ToArray();
        SetupAiResponse(captured with { ToolCalls = [calls[0]] });
        var round = 0;
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(invocation =>
            {
                DeclaredAskReasons(new AiToolRequest("", "", invocation.Arg<AiConversationContext>().ToolDeclarationUpdates))
                    .Should().Contain("record_selection");
                if (round == 1)
                    invocation.ArgAt<IReadOnlyList<AiToolCallResult>>(1).Should().ContainSingle()
                        .Which.Error.Should().Contain("text is too long");
                return Result.Success(captured with { ToolCalls = [calls[++round]] });
            });
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["habitList", "choice"], SupportsHabitListCard: true, Locale: language);

        var result = await handler.Handle(new(UserId, "Continue.", ClientContext: client), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<AstraChoiceBlock>().Which.Options.Should().OnlyContain(option =>
                option.Description == (language == "en" ? "Same details" : "Mesmos dados"));
        round.Should().Be(2);
        AssertStoredComposition(result.Value, client, language);
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    public async Task G14_RejectedHabitSetupRecoversTheRequestedReadOrRecordChoice(string language, bool choice)
    {
        SetupExistingParents(language);
        EnableComposition();
        var habits = new[] { CreateHabit(language == "en" ? "Water" : "Beber água"), CreateHabit(language == "en" ? "Walk" : "Caminhada") };
        SetupSelectionHabits(habits);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(RealAskTool(), query);
        UseProductionReadExecutor(query);
        SetupAiResponse(CapturedCompositionPlan(choice ? "prod-r6-composition-choice-pt-BR-r2" : "prod-r6-composition-proactive-pt-BR-r2"));
        var read = ToolResponse(query.Name, "read", JsonSerializer.Serialize(new { for_selection = choice, orbit_read_complete = !choice }));
        var select = ToolResponse("ask", "choose", JsonSerializer.Serialize(new
        {
            input = new
            {
                missing_input = "record_selection",
                form = "choice",
                allowFreeText = true,
                question = language == "en" ? "Which habit and what change?" : "Qual hábito e qual mudança?",
                options = habits.Select(habit => new { id = habit.Id.ToString(), label = habit.Title }).ToArray()
            }
        }));
        var rounds = 0;
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(invocation =>
            {
                var feedback = invocation.ArgAt<IReadOnlyList<AiToolCallResult>>(1).Should().ContainSingle().Which;
                if (rounds == 0)
                    feedback.Success.Should().BeFalse();
                else
                    feedback.Success.Should().BeTrue(feedback.Error);
                return Result.Success(rounds++ == 0 ? read : select);
            });
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["habitList", "choice", "question"],
            SupportsHabitListCard: true, Locale: language);
        var events = new List<ChatStreamEvent>();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        var result = await handler.Handle(new(UserId, "Continue.", ClientContext: client,
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        elapsed.ElapsedMilliseconds.Should().BeLessThan(10_000);
        result.IsSuccess.Should().BeTrue(result.Error);
        var block = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Subject;
        block.Kind.Should().Be(choice ? "choice" : "habitList");
        if (choice)
            block.Block.Should().BeOfType<AstraChoiceBlock>().Which.Options.Select(option => option.Id)
                .Should().Equal(habits.Select(habit => habit.Id.ToString()));
        else
            block.Block.Should().BeOfType<HabitListCard>().Which.Items.Select(item => item.Id)
                .Should().BeEquivalentTo(habits.Select(habit => habit.Id.ToString()));
        rounds.Should().Be(choice ? 2 : 1);
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        events.Should().NotContain(item => item.Type == "delta");
        await _operationExecutor.Received(1).ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId == query.Name), Arg.Any<CancellationToken>());
        AssertStoredComposition(result.Value, client, language);
    }

    [Fact]
    public async Task G14_FreshExplicitFullSetReadAllowsDuplicateTitleBulkPreview()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var habits = new[] { CreateHabit("Caminhada"), CreateHabit("Caminhada") };
        SetupSelectionHabits(habits);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var write = new BulkLogHabitsTool(_mediator, _habitRepo, _userDateService,
            new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(), Substitute.For<IIdempotencyStore>(), _unitOfWork));
        var handler = CreateHandler(query, write);
        var held = HoldSelectionReplayWrites(query, write);
        var captured = CapturedCompositionPlan("prod-r6-ambiguity-pt-r1").ToolCalls!.Single();
        var args = JsonNode.Parse(captured.Args.GetRawText())!.AsObject();
        args["habit_ids"] = new JsonArray(habits.Select(habit => JsonValue.Create(habit.Id.ToString())).ToArray<JsonNode?>());
        args["date"] = Today.ToString("yyyy-MM-dd");
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = [new(query.Name, "full-set", ParseArguments("""{"for_selection":false,"orbit_read_complete":false}""")),
                captured with { Args = JsonSerializer.SerializeToElement(args) }]
        });
        ContinueWithReadDirective("");

        var result = await handler.Handle(new(UserId, "Continue.", ClientContext: new(SupportsParts: true,
            SupportsPendingOperationChanges: true, SupportsHabitListCard: true, PartKinds: ["habitList", "choice"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        held.Should().ContainSingle().Which.OperationId.Should().Be(write.Name);
        result.Value.PendingOperations.Should().ContainSingle().Which.Items!.Select(item => item.EntityId)
            .Should().BeEquivalentTo(habits.Select(habit => (Guid?)habit.Id));
        result.Value.Actions.Should().BeEmpty();
    }
}
