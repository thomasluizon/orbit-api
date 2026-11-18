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
        _promptBuilder.BuildChangePlanStatic(Arg.Any<PromptBuildRequest>()).Returns("short prompt");
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
        _promptBuilder.DidNotReceive().BuildStatic(Arg.Any<PromptBuildRequest>());
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
        _promptBuilder.DidNotReceive().BuildChangePlanStatic(Arg.Any<PromptBuildRequest>());
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

    [Theory]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    public async Task AskFormMap_DraftBindingStaysOnTodaysCreateResult(string language, bool bound)
    {
        SetupExistingParents(language);
        EnableChangePlan(FeatureFlagKeys.AiBlockFirst, FeatureFlagKeys.AiConversationRecording);
        var create = new CreateHabitTool(_userDateService, _mediator);
        var handler = CreateHandler(create, RealAskTool());
        var title = language == "en" ? "Walking habit" : "Hábito de caminhada";
        var clarificationId = Guid.NewGuid();
        _pendingClarificationStore.CreateAsync(UserId, create.Name, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(clarificationId);
        var issued = new List<IssuedAiBlock>();
        _blockStore.AddAsync(Arg.Do<IssuedAiBlock>(issued.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        SetupAiResponse(ToolResponse(create.Name, "new-draft", JsonSerializer.Serialize(new { title })) with { ConversationContext = new() });
        var conversations = new List<AiConversationContext>();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Do<AiConversationContext>(conversations.Add),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ToolResponse("ask", "setup", JsonSerializer.Serialize(new
            {
                input = new
                {
                    form = "choice",
                    missing_input = "habit_setup",
                    draft_call_id = bound ? "new-draft" : "another-draft",
                    missing_field = "frequency_unit",
                    question = language == "en" ? "How often?" : "Com que frequência?",
                    options = new[] { new { id = "day", label = language == "en" ? "Daily" : "Todo dia" }, new { id = "week", label = language == "en" ? "Weekly" : "Toda semana" } }
                }
            }))), Result.Success(new AiResponse { TextMessage = "How often?" }));

        var result = await handler.Handle(new(UserId, "Continue.",
            ClientContext: new AgentClientContext(SupportsParts: true, PartKinds: ["choice"], Locale: language)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        conversations.Should().OnlyContain(conversation => conversation.ToolDeclarationUpdates.Count == 0,
            "the flag-path ask schema never changes during a turn");
        var definition = issued.Should().ContainSingle().Subject.ReadDefinition();
        if (bound)
        {
            definition.DraftClarification.Should().Be(new AiDraftClarification("new-draft", "frequency_unit", clarificationId));
            result.Value.Actions.Should().NotContain(action => action.Status == ActionStatus.NeedsClarification);
        }
        else
        {
            definition.DraftClarification.Should().BeNull();
        }
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
