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
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

/// <summary>
/// The provider request, the message routes and the <c>ask</c> forms of the <c>ai_change_plan</c> path
/// (thomasluizon/orbit-tickets#2159).
/// </summary>
public partial class ProcessUserChatCommandHandlerTests
{
    private async Task<AiToolRequest> CaptureTurnRequestAsync(bool changePlan, string message = "Show my habits")
    {
        if (changePlan)
            EnableChangePlan();
        SetupUserAndPayGate();
        _promptBuilder.BuildStatic(Arg.Is<PromptBuildRequest>(prompt => prompt.ChangePlan)).Returns("short prompt");
        SetupAiResponse(new AiResponse { TextMessage = "Here they are." });
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(query, RealAskTool());

        var result = await handler.Handle(new(UserId, message,
            ClientContext: new(SupportsParts: true, PartKinds: ["choice", "question", "pickList"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        return _aiIntentService.ReceivedCalls().SelectMany(call => call.GetArguments().OfType<AiToolRequest>()).Single();
    }

    private static JsonElement Declaration(AiToolRequest request, string name) => request.ToolDeclarations
        .Select(declaration => JsonSerializer.SerializeToElement(declaration))
        .Single(declaration => declaration.GetProperty("name").GetString() == name);

    [Fact]
    public async Task ChangePlanRequest_CarriesThePathTheShortPromptAndTheFlagDeclarations()
    {
        var request = await CaptureTurnRequestAsync(changePlan: true);

        request.ChangePlan.Should().BeTrue();
        request.Purpose.Should().Be("chat");
        request.SystemPrompt.Should().StartWith("short prompt");
        _promptBuilder.Received().BuildStatic(Arg.Is<PromptBuildRequest>(prompt => prompt.ChangePlan));
        _promptBuilder.DidNotReceive().BuildStatic(Arg.Is<PromptBuildRequest>(prompt => !prompt.ChangePlan));
        var query = Declaration(request, "query_habits");
        query.GetProperty("description").GetString().Should().Be(ChangePlanToolDescriptions.QueryHabits);
        query.GetProperty("parameters").GetProperty("properties").GetProperty("for_selection").GetProperty("description").GetString()
            .Should().Be(ChangePlanToolDescriptions.ForSelection);
        var ask = Declaration(request, "ask");
        ask.GetProperty("description").GetString().Should().Be(ChangePlanToolDescriptions.Ask);
        var forms = ask.GetProperty("parameters").GetProperty("properties").GetProperty("input").GetProperty("anyOf").EnumerateArray().ToArray();
        static bool Has(JsonElement form, string field) => form.GetProperty("properties").TryGetProperty(field, out var _);
        forms.Count(form => Has(form, "about")).Should().Be(1);
        forms.Where(form => !Has(form, "about")).Select(form => Has(form, "draft_call_id")).Should().AllBeEquivalentTo(true);
        JsonSerializer.Serialize(forms).Should().NotContain("target_ids").And.NotContain("Record and bulk selection require query_habits");
        request.SystemPrompt.Should().NotContain(AskTool.AccountReadInstruction)
            .And.Contain("Supported ask forms: choice, pickList, question");
    }

    [Fact]
    public async Task OldPathRequest_KeepsTodaysPromptPathAndDeclarations()
    {
        var request = await CaptureTurnRequestAsync(changePlan: false);

        request.ChangePlan.Should().BeFalse();
        request.SystemPrompt.Should().StartWith("static prompt");
        _promptBuilder.DidNotReceive().BuildStatic(Arg.Is<PromptBuildRequest>(prompt => prompt.ChangePlan));
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        Declaration(request, "query_habits").GetProperty("description").GetString().Should().Be(query.Description);
        var ask = Declaration(request, "ask");
        ask.GetProperty("description").GetString().Should().Be(RealAskTool().Description);
        JsonSerializer.Serialize(ask).Should().NotContain("\"about\"").And.Contain("target_ids");
        request.SystemPrompt.Should().Contain(AskTool.AccountReadInstruction);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FaqMessage_ReachesTheModelOnlyOnTheFlag(bool changePlan)
    {
        if (changePlan)
            EnableChangePlan();
        SetupUserAndPayGate();
        ChatFaqCache.StoreAnswer("bad_habits", "pt", "Resposta em cache");
        SetupAiResponse(new AiResponse { TextMessage = "Um mau hábito é algo que você quer reduzir." });
        var handler = CreateHandler();

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "O que e um mau habito?"), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        if (changePlan)
        {
            result.Value.AiMessage.Should().Be("Um mau hábito é algo que você quer reduzir.");
            await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Any<AiToolRequest>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        }
        else
        {
            result.Value.AiMessage.Should().Be("Resposta em cache");
            await _aiIntentService.DidNotReceiveWithAnyArgs().SendWithToolsAsync(default!, default, default);
        }
    }

    private async Task<(Result<ChatResponse> Result, List<IssuedAiBlock> Issued, List<IReadOnlyList<AiToolCallResult>> ToolResults)>
        RunFlagAskAsync(object input, string[] partKinds, params Orbit.Application.Chat.Tools.IAiTool[] tools)
    {
        var issued = new List<IssuedAiBlock>();
        _blockStore.AddAsync(Arg.Do<IssuedAiBlock>(issued.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = [new("ask", "ask", JsonSerializer.SerializeToElement(new { input }))]
        });
        var toolResults = new List<IReadOnlyList<AiToolCallResult>>();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Do<IReadOnlyList<AiToolCallResult>>(results => toolResults.Add([.. results])),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Tell me." }));
        var result = await CreateHandler([RealAskTool(), .. tools]).Handle(new(UserId, "Help me",
            ClientContext: new(SupportsParts: true, PartKinds: partKinds)), default);
        return (result, issued, toolResults);
    }

    [Theory]
    [InlineData("habit_setup")]
    [InlineData("preference")]
    [InlineData("check_in")]
    [InlineData("record_selection")]
    [InlineData("bulk_selection")]
    public async Task AskFormMap_ChoiceWithLabelsShowsForEveryMissingInputLabelWithNoRead(string missingInput)
    {
        EnableChangePlan();
        SetupUserAndPayGate();

        var run = await RunFlagAskAsync(new
        {
            form = "choice",
            missing_input = missingInput,
            question = "When?",
            multiSelect = true,
            allowFreeText = true,
            options = new[] { new { id = "morning", label = "Morning" }, new { id = "evening", label = "Evening" } }
        }, ["choice"]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        var definition = run.Issued.Should().ContainSingle().Subject.ReadDefinition();
        definition.Form.Should().Be(AiBlockKinds.Choice);
        definition.Options.Select(option => option.Id).Should().Equal("morning", "evening");
        definition.MultiSelect.Should().BeTrue();
        definition.AllowFreeText.Should().BeTrue();
        run.ToolResults.Should().BeEmpty();
    }

    [Fact]
    public async Task AskFormMap_QuestionShowsUnchangedWithNoRead()
    {
        EnableChangePlan();
        SetupUserAndPayGate();

        var run = await RunFlagAskAsync(new
        {
            form = "question",
            missing_input = "preference",
            questions = new object[]
            {
                new { id = "time", type = "singleChoice", prompt = "When?", options = new[] { new { id = "am", label = "Morning" }, new { id = "pm", label = "Evening" } } },
                new { id = "energy", type = "scale", prompt = "Energy?", lowLabel = "Low", highLabel = "High" }
            }
        }, ["question"]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        var definition = run.Issued.Should().ContainSingle().Subject.ReadDefinition();
        definition.Form.Should().Be(AiBlockKinds.Question);
        definition.Questions!.Select(question => question.Id).Should().Equal("time", "energy");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task AskFormMap_PickListShowsUnchangedWithNoRead(string language)
    {
        var habits = SetupPickListTurn(language);
        EnableChangePlan(FeatureFlagKeys.AiBlockFirst, FeatureFlagKeys.AiConversationRecording);
        var input = JsonSerializer.SerializeToElement(ModelPickList(habits, "log_habits").Args).GetProperty("input");

        var run = await RunFlagAskAsync(input, ["pickList"]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        var definition = run.Issued.Should().ContainSingle().Subject.ReadDefinition();
        definition.Form.Should().Be(AiBlockKinds.PickList);
        definition.Action.Should().Be("log_habits");
        definition.Items.Select(item => item.Id).Should().Equal(habits.Select(habit => habit.Id.ToString()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AskFormMap_ChoiceAboutRecordsTakesSeveralInPlaceOfMultiSelect(bool several)
    {
        var run = await RunAboutAskAsync(3, "en", ["choice", "question"], (children, _) => children, several: several);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        var definition = run.Issued.Should().ContainSingle().Subject.ReadDefinition();
        definition.Form.Should().Be(AiBlockKinds.Choice);
        definition.MultiSelect.Should().Be(several);
        definition.Options.Select(AiChoiceSelection.TargetsOf).SelectMany(targets => targets)
            .Should().BeEquivalentTo(run.Children.Select(child => child.Id.ToString()));
    }

    private sealed record DraftCreateTurn(ProcessUserChatCommandHandler Handler, Result<ChatResponse> Result, string Title,
        Guid ClarificationId, List<IssuedAiBlock> Issued, List<IReadOnlyList<AiToolCallResult>> ToolResults,
        List<AiConversationContext> Conversations, CreateHabitTool Create, BulkCreateHabitsTool Bulk);

    /// <summary>
    /// One turn on the <c>ai_change_plan</c> path: the model sends a create change with a title only, gets the compile
    /// error, and asks for the frequency with <paramref name="draftCallId"/>. The clarification store keeps the draft that
    /// the turn stashes, as the real store does.
    /// </summary>
    private async Task<DraftCreateTurn> RunDraftCreateTurnAsync(string language, ChangeDeclarationShape shape, string draftCallId)
    {
        SetupExistingParents(language);
        EnableChangePlan(FeatureFlagKeys.AiBlockFirst, FeatureFlagKeys.AiConversationRecording);
        _changeShape = shape;
        var create = new CreateHabitTool(_userDateService, _mediator);
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(create, bulk, RealAskTool());
        var title = language == "en" ? "Walking habit" : "Hábito de caminhada";
        var clarificationId = Guid.NewGuid();
        PendingClarificationData? stashed = null;
        _pendingClarificationStore.CreateAsync(UserId, create.Name, Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                stashed = new(create.Name, call.ArgAt<string>(2), call.ArgAt<string>(3),
                    [.. JsonSerializer.Deserialize<QuickAction[]>(call.ArgAt<string>(5))!.Select(action => action.Value)],
                    DateTime.UtcNow.AddMinutes(30));
                return clarificationId;
            });
        _pendingClarificationStore.GetForResolutionAsync(clarificationId, UserId, Arg.Any<CancellationToken>()).Returns(_ => stashed);
        var issued = new List<IssuedAiBlock>();
        _blockStore.AddAsync(Arg.Do<IssuedAiBlock>(issued.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        SetupAiResponse(ChangeList(shape, null, false, ("new-draft", "bulk_create_habits", new { habits = new[] { new { title } } })));
        var conversations = new List<AiConversationContext>();
        var toolResults = new List<IReadOnlyList<AiToolCallResult>>();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Do<AiConversationContext>(conversations.Add),
                Arg.Do<IReadOnlyList<AiToolCallResult>>(results => toolResults.Add([.. results])),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ToolResponse("ask", "setup", JsonSerializer.Serialize(new
            {
                input = new
                {
                    form = "choice",
                    missing_input = "habit_setup",
                    draft_call_id = draftCallId,
                    missing_field = "frequency_unit",
                    question = language == "en" ? "How often?" : "Com que frequência?",
                    options = new[] { new { id = "day", label = language == "en" ? "Daily" : "Todo dia" }, new { id = "week", label = language == "en" ? "Weekly" : "Toda semana" } }
                }
            }))), Result.Success(new AiResponse { TextMessage = "How often?" }));

        var result = await handler.Handle(new(UserId, "Continue.",
            ClientContext: new AgentClientContext(SupportsParts: true, PartKinds: ["choice"], Locale: language)), default);

        return new(handler, result, title, clarificationId, issued, toolResults, conversations, create, bulk);
    }

    /// <summary>
    /// The draft binding of the change list: a create change with a missing required field is a compile error that names
    /// the field, the list holds nothing, and an <c>ask</c> binds to the change id and that field. An <c>ask</c> that names
    /// another draft binds to nothing.
    /// </summary>
    [Theory]
    [InlineData("en", true, ChangeDeclarationShape.Kinds)]
    [InlineData("pt-BR", true, ChangeDeclarationShape.Kinds)]
    [InlineData("en", false, ChangeDeclarationShape.Kinds)]
    [InlineData("pt-BR", false, ChangeDeclarationShape.Kinds)]
    [InlineData("en", true, ChangeDeclarationShape.Wrapper)]
    [InlineData("pt-BR", true, ChangeDeclarationShape.Wrapper)]
    public async Task AskFormMap_DraftBindsToTheCompileErrorOfACreateChange(string language, bool bound, ChangeDeclarationShape shape)
    {
        var turn = await RunDraftCreateTurnAsync(language, shape, bound ? "new-draft" : "another-draft");

        turn.Result.IsSuccess.Should().BeTrue(turn.Result.Error);
        var compiled = turn.ToolResults[0].Should().ContainSingle().Subject;
        compiled.Success.Should().BeFalse();
        compiled.Error.Should().Be(ChangeListCompiler.NotPrepared);
        JsonSerializer.SerializeToElement(compiled.Payload).GetProperty("errors").Deserialize<ChangeError[]>().Should()
            .Equal(new ChangeError("new-draft", "frequency_unit", ChangeListCompiler.MissingField,
                $"The new habit {turn.Title} needs a value for frequency_unit."));
        turn.Conversations.Should().OnlyContain(conversation => conversation.ToolDeclarationUpdates.Count == 0,
            "the flag-path ask schema never changes during a turn");
        turn.Result.Value.PendingOperations.Should().BeNullOrEmpty();
        turn.Result.Value.Actions.Should().NotContain(action => action.Status == ActionStatus.NeedsClarification);
        var definition = turn.Issued.Should().ContainSingle().Subject.ReadDefinition();
        if (bound)
            definition.DraftClarification.Should().Be(new AiDraftClarification("new-draft", "frequency_unit", turn.ClarificationId));
        else
            definition.DraftClarification.Should().BeNull();
    }

    /// <summary>
    /// The last step of the draft binding: the answer of the bound question starts a turn that holds the stored draft with
    /// the answered field. The model sends the create change of that draft, and the change list prepares the preview of
    /// the new habit. The answer saves no habit.
    /// </summary>
    [Theory]
    [InlineData("en", ChangeDeclarationShape.Kinds)]
    [InlineData("pt-BR", ChangeDeclarationShape.Kinds)]
    [InlineData("en", ChangeDeclarationShape.Wrapper)]
    [InlineData("pt-BR", ChangeDeclarationShape.Wrapper)]
    public async Task AskFormMap_TheAnswerOfABoundDraftCreatesThePreviewOfTheCreateChange(string language, ChangeDeclarationShape shape)
    {
        var turn = await RunDraftCreateTurnAsync(language, shape, "new-draft");
        turn.Result.IsSuccess.Should().BeTrue(turn.Result.Error);
        var block = turn.Issued.Should().ContainSingle().Subject;
        block.ReadDefinition().DraftClarification.Should().Be(new AiDraftClarification("new-draft", "frequency_unit", turn.ClarificationId));
        _unitOfWork.PassThroughTransactions<Result<PreparedAiBlockAnswer>>();
        _blockStore.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        _blockStore.MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(call => { block.MarkAnswered(call.ArgAt<DateTime>(2)); return true; });
        _blockStore.CheckpointAnswerAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var outcomes = R9fWriteExecutor(turn.Create, turn.Bulk);
        var requests = new List<AiToolRequest>();
        _aiIntentService.SendWithToolsAsync(Arg.Do<AiToolRequest>(requests.Add), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call => Result.Success(ChangeList(shape, null, true,
                ("answered-draft", "bulk_create_habits", new { habits = new[] { ValidatedDraft(call.Arg<AiToolRequest>()) } }))));

        var answer = await turn.Handler.Handle(new(UserId, string.Empty, BlockAnswer: new AiBlockAnswer(block.Id, ["day"]),
            ClientContext: new AgentClientContext(SupportsParts: true, SupportsPendingOperationChanges: true, PartKinds: ["choice"], Locale: language)), default);

        answer.IsSuccess.Should().BeTrue(answer.Error);
        var draft = ValidatedDraft(requests.Should().ContainSingle().Subject);
        draft.GetProperty("title").GetString().Should().Be(turn.Title);
        draft.GetProperty("frequency_unit").GetString().Should().Be("Day");
        var held = outcomes.Should().ContainSingle().Subject;
        held.Operation.OperationId.Should().Be(turn.Create.Name);
        held.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        var preview = answer.Value.PendingOperations.Should().ContainSingle().Subject;
        preview.Id.Should().Be(held.PendingOperation!.Id);
        var item = preview.Items.Should().ContainSingle().Subject;
        item.EntityName.Should().Be(turn.Title);
        item.Fields.Should().Contain(field => field.Field == "frequency_unit" && field.NewValue == "Day");
        answer.Value.Actions.Should().NotContain(action => action.Status == ActionStatus.Success);
        answer.Value.AiMessage.Should().NotBe(NotPreparedFor(language));
        await _habitRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _pendingClarificationStore.Received(1).GetForResolutionAsync(turn.ClarificationId, UserId, Arg.Any<CancellationToken>());
    }

    /// <summary>The arguments of the habit draft that the server gives the model in the turn of a draft answer.</summary>
    private static JsonElement ValidatedDraft(AiToolRequest request)
    {
        const string prefix = "Validated habit draft: ";
        var context = request.DynamicContext!;
        using var draft = JsonDocument.Parse(context[(context.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..]);
        return draft.RootElement.GetProperty("arguments").Clone();
    }

    [Fact]
    public void ChangePlanReadPayload_KeepsItsDataAndLosesItsInstruction()
    {
        var templates = Enumerable.Range(0, 400).Select(index => new Orbit.Application.ChecklistTemplates.Queries.ChecklistTemplateResponse(
            Guid.NewGuid(), $"Template {index} with a long name", [.. Enumerable.Range(0, 20).Select(item => $"Item {item} of a long list")])).ToArray();
        var payload = ChecklistTemplateModelPayload.From(templates);

        var stripped = JsonSerializer.SerializeToElement(ProcessUserChatCommandHandler.WithoutInstruction(payload));

        stripped.TryGetProperty("instruction", out _).Should().BeFalse();
        stripped.GetProperty("totalCount").GetInt32().Should().Be(400);
        stripped.GetProperty("templates").GetArrayLength().Should().Be(payload.Templates.Count);
        JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .GetProperty("instruction").GetString().Should().NotBeNullOrEmpty();
        ProcessUserChatCommandHandler.WithoutInstruction("plain data").Should().Be("plain data");
    }
}
