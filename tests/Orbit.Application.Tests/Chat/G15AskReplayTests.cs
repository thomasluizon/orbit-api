using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Tests.Common;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    public static IEnumerable<object[]> G15UnboundQuestions() =>
        from capture in new[] { "composition-choice-pt-BR-r1", "composition-choice-pt-BR-r3",
            "composition-proactive-pt-BR-r1", "composition-proactive-pt-BR-r2", "composition-proactive-pt-BR-r3",
            "composition-coaching-advice-pt-BR-r1", "composition-coaching-advice-pt-BR-r2", "composition-coaching-advice-pt-BR-r3" }
        from language in new[] { "en", "pt-BR" }
        select new object[] { capture, language };

    [Theory]
    [MemberData(nameof(G15UnboundQuestions))]
    public async Task G15_CapturedSetupQuestionsNeedATypedCreateResult(string capture, string language)
    {
        SetupExistingParents(language);
        EnableComposition();
        SetupAiResponse(CapturedCompositionPlan("prod-r7-" + capture) with { ConversationContext = new() });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = language == "en" ? "Start with one easy action." : "Comece com uma ação fácil." }));
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["choice", "question"], Locale: language);

        var result = await CreateHandler(RealAskTool()).Handle(new(UserId, "Continue.", ClientContext: client), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var request = _aiIntentService.ReceivedCalls().SelectMany(call => call.GetArguments().OfType<AiToolRequest>()).Single();
        request.ToolDeclarations.Select(declaration => JsonSerializer.SerializeToElement(declaration).GetProperty("name").GetString())
            .Should().NotContain("ask");
        result.Value.Parts!.OfType<ChatBlockPart>().Should().BeEmpty();
        await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Count == 1 && !results[0].Success
                && results[0].Error!.Contains("typed missing-field result")), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        AssertStoredComposition(result.Value, client, language);
    }

    [Theory]
    [InlineData("en", "valid")]
    [InlineData("pt-BR", "valid")]
    [InlineData("en", "other-draft")]
    [InlineData("pt-BR", "other-draft")]
    [InlineData("en", "other-field")]
    [InlineData("pt-BR", "other-field")]
    [InlineData("en", "other-reason")]
    [InlineData("pt-BR", "other-reason")]
    [InlineData("en", "unbound-question")]
    [InlineData("pt-BR", "unbound-question")]
    public async Task G15_RealCreateMissingFieldDeclaresABoundSetupChoice(string language, string variant)
    {
        SetupExistingParents(language);
        EnableComposition();
        var create = new CreateHabitTool(_userDateService, _mediator);
        var handler = CreateHandler(create, RealAskTool());
        var title = language == "en" ? "Walking habit" : "Hábito de caminhada";
        _pendingClarificationStore.CreateAsync(UserId, create.Name, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        SetupAiResponse(ToolResponse(create.Name, "new-draft", JsonSerializer.Serialize(new { title })) with { ConversationContext = new() });
        var rounds = 0;
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(invocation =>
            {
                if (rounds++ > 0) return Result.Success(new AiResponse { TextMessage = language == "en" ? "How often?" : "Com que frequência?" });
                var conversation = invocation.Arg<AiConversationContext>();
                var declaration = JsonSerializer.SerializeToElement(conversation.ToolDeclarationUpdates.Should().ContainSingle().Subject);
                declaration.GetProperty("name").GetString().Should().Be("ask");
                var forms = declaration.GetProperty("parameters").GetProperty("properties").GetProperty("input").GetProperty("anyOf");
                foreach (var form in forms.EnumerateArray())
                {
                    var fields = form.GetProperty("properties");
                    fields.GetProperty("missing_input").GetProperty("enum").EnumerateArray().Select(value => value.GetString()).Should().Equal("habit_setup");
                    fields.GetProperty("draft_call_id").GetProperty("enum").EnumerateArray().Select(value => value.GetString()).Should().Equal("new-draft");
                    fields.GetProperty("missing_field").GetProperty("enum").EnumerateArray().Select(value => value.GetString()).Should().Equal("frequency_unit");
                    fields.TryGetProperty("proposed_habit_title", out _).Should().BeFalse();
                }
                var response = ToolResponse("ask", "setup", JsonSerializer.Serialize(new
                {
                    input = new
                    {
                        form = "choice",
                        missing_input = variant == "other-reason" ? "check_in" : "habit_setup",
                        draft_call_id = variant == "other-draft" ? "another-draft" : "new-draft",
                        missing_field = variant == "other-field" ? "due_time" : "frequency_unit",
                        question = language == "en" ? "How often?" : "Com que frequência?",
                        options = new[] { new { id = "day", label = language == "en" ? "Daily" : "Todo dia" }, new { id = "week", label = language == "en" ? "Weekly" : "Toda semana" } }
                    }
                }));
                if (variant == "unbound-question") response = ToolResponse("ask", "setup",
                    """{"input":{"form":"question","missing_input":"habit_setup","draft_call_id":"new-draft","missing_field":"frequency_unit","questions":[{"id":"unrelated","type":"yesNo","prompt":"Continue?"}]}}""");
                return Result.Success(response);
            });
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["choice"], Locale: language);

        var result = await handler.Handle(new(UserId, "Continue.", ClientContext: client), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        if (variant == "valid")
        {
            result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Which.Kind.Should().Be("choice");
            result.Value.Actions.Should().NotContain(action => action.Status == ActionStatus.NeedsClarification);
        }
        else
        {
            result.Value.Parts!.OfType<ChatBlockPart>().Should().BeEmpty();
            await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
            rounds.Should().Be(2);
        }
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await _habitRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        AssertStoredComposition(result.Value, client, language);
    }

    [Theory]
    [InlineData("en", "choice")]
    [InlineData("pt-BR", "choice")]
    [InlineData("en", "singleChoice")]
    [InlineData("pt-BR", "singleChoice")]
    [InlineData("en", "freeText")]
    [InlineData("pt-BR", "freeText")]
    public async Task G15_BoundAnswerRecoversTheStoredImageDraftOnTheNextTurn(string language, string answerType)
    {
        SetupExistingParents(language);
        EnableComposition();
        var title = language == "en" ? "Plant watering habit" : "Hábito de regar as plantas";
        var description = language == "en" ? "Use the small blue watering can" : "Usar o regador azul pequeno";
        var arguments = JsonSerializer.SerializeToElement(new
        {
            title,
            description,
            emoji = "🪴",
            frequency_quantity = 2,
            due_time = "08:30",
            reminder_enabled = true,
            tag_names = new[] { "Home" }
        });
        _unitOfWork.PassThroughTransactions<Result<PreparedAstraBlockAnswer>>();
        var clarificationId = Guid.NewGuid();
        PendingClarificationData? pending = null;
        _pendingClarificationStore.CreateAsync(UserId, "create_habit", Arg.Any<string>(), "frequency_unit",
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var actions = JsonSerializer.Deserialize<QuickAction[]>(call.ArgAt<string>(5))!;
                pending = new("create_habit", call.ArgAt<string>(2), "frequency_unit",
                    actions.Select(action => action.Value).ToArray(), DateTime.UtcNow.AddMinutes(30));
                return clarificationId;
            });
        _pendingClarificationStore.GetForResolutionAsync(clarificationId, UserId, Arg.Any<CancellationToken>())
            .Returns(_ => pending);
        Orbit.Domain.Entities.IssuedAstraBlock? issued = null;
        _blockStore.When(store => store.AddAsync(Arg.Any<Orbit.Domain.Entities.IssuedAstraBlock>(), Arg.Any<CancellationToken>()))
            .Do(call => issued = call.Arg<Orbit.Domain.Entities.IssuedAstraBlock>());
        var prompt = language == "en" ? "How often?" : "Com que frequência?";
        var options = new[] { new { id = "day", label = language == "en" ? "Daily" : "Todo dia" },
            new { id = "week", label = language == "en" ? "Weekly" : "Toda semana" } };
        var ask = answerType == "choice"
            ? JsonSerializer.SerializeToElement(new { form = "choice", question = prompt, options })
            : JsonSerializer.SerializeToElement(new
            {
                form = "question",
                questions = new[] {
                new { id = "frequency_unit", type = "singleChoice", prompt,
                    options, allowFreeText = answerType == "freeText" } }
            });
        var askNode = System.Text.Json.Nodes.JsonNode.Parse(ask.GetRawText())!.AsObject();
        askNode["missing_input"] = "habit_setup";
        askNode["draft_call_id"] = "image-draft";
        askNode["missing_field"] = "frequency_unit";
        SetupAiResponse(ToolResponse("create_habit", "image-draft", arguments.GetRawText()) with { ConversationContext = new() });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ToolResponse("ask", "setup", JsonSerializer.Serialize(new { input = askNode }))));
        var client = new AgentClientContext(SupportsParts: true,
            PartKinds: [answerType == "choice" ? "choice" : "question"], Locale: language);
        var handler = CreateHandler(new CreateHabitTool(_userDateService, _mediator), RealAskTool());

        var first = await handler.Handle(new(UserId, "Create the habit in this image.",
            ImageData: [1, 2, 3], ImageMimeType: "image/png", ClientContext: client), default);

        first.IsSuccess.Should().BeTrue(first.Error);
        issued.Should().NotBeNull(JsonSerializer.Serialize(first.Value));
        pending.Should().NotBeNull();
        issued!.ReadDefinition().DraftClarification.Should().Be(new AstraDraftClarification("image-draft", "frequency_unit", clarificationId));
        first.Value.Actions.Should().NotContain(action => action.Status == ActionStatus.NeedsClarification);
        var visible = JsonSerializer.Serialize(first.Value, ChatStreamEvent.SerializerOptions);
        visible.Should().NotContain(clarificationId.ToString()).And.NotContain(description).And.NotContain("draft_call_id");
        _blockStore.GetAsync(issued!.Id, UserId, Arg.Any<CancellationToken>()).Returns(issued);
        _blockStore.MarkAnsweredAsync(issued.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
        var freeText = language == "en" ? "Every day" : "Todos os dias";
        var answer = answerType == "choice" ? new AstraBlockAnswer(issued.Id, ["day"])
            : new AstraBlockAnswer(issued.Id, [], Answers: [new AstraQuestionAnswer("frequency_unit",
                SelectedIds: answerType == "singleChoice" ? ["day"] : null,
                FreeText: answerType == "freeText" ? freeText : null)]);
        AiToolRequest? continuation = null;
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                continuation = call.Arg<AiToolRequest>();
                return Result.Success(new AiResponse { TextMessage = "OK" });
            });

        var second = await handler.Handle(new(UserId, string.Empty, ClientContext: client, BlockAnswer: answer), default);

        second.IsSuccess.Should().BeTrue(second.Error);
        continuation.Should().NotBeNull();
        continuation!.ImageData.Should().BeNull();
        continuation.History.Should().BeNullOrEmpty();
        const string prefix = "Validated habit draft: ";
        continuation.DynamicContext.Should().Contain(prefix);
        var draftJson = continuation.DynamicContext![(continuation.DynamicContext.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..];
        using var context = JsonDocument.Parse(draftJson);
        var merged = context.RootElement.GetProperty("arguments");
        context.RootElement.GetProperty("toolName").GetString().Should().Be("create_habit");
        foreach (var supplied in arguments.EnumerateObject())
            merged.GetProperty(supplied.Name).GetRawText().Should().Be(supplied.Value.GetRawText());
        merged.GetProperty("frequency_unit").GetString().Should().Be(answerType == "freeText" ? freeText : "Day");
        continuation.UserMessage.Should().NotContain(title).And.NotContain(description).And.NotContain(clarificationId.ToString());
        continuation.DynamicContext.Should().Contain("Selection is not write approval");
        await _pendingClarificationStore.Received(1).GetForResolutionAsync(clarificationId, UserId, Arg.Any<CancellationToken>());
        await _habitRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _operationExecutor.ReceivedWithAnyArgs(1).ExecuteAsync(default!, default);
        _mediator.ReceivedCalls().Should().BeEmpty();
        var captured = _conversationRecorder.ReceivedCalls().SelectMany(call => call.GetArguments().OfType<ConversationTurnCapture>()).Last();
        captured.UserContent.Should().NotContain(title).And.NotContain(description).And.NotContain(clarificationId.ToString());
    }

    [Fact]
    public async Task G15_CapturedInitialTruncationCannotGenerateAnUnboundedDraftTitle()
    {
        SetupExistingParents("pt-BR");
        EnableComposition();
        var capture = CapturedCompositionPlan("prod-r7-composition-choice-pt-BR-r2");
        capture.ToolCalls.Should().BeEmpty();
        SetupAiResponse(capture with { IsTruncated = true });

        var result = await CreateHandler(RealAskTool()).Handle(new(UserId, "Continue.", ClientContext: new(SupportsParts: true,
            PartKinds: ["choice", "question"], Locale: "pt-BR")), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var request = _aiIntentService.ReceivedCalls().SelectMany(call => call.GetArguments().OfType<AiToolRequest>()).Single();
        JsonSerializer.Serialize(request.ToolDeclarations).Should().NotContain("proposed_habit_title");
        request.ToolDeclarations.Should().BeEmpty();
        result.Value.Parts!.OfType<ChatBlockPart>().Should().BeEmpty();
        await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await AssertNoCompositionContinuation();
    }
}
