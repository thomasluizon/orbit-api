using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task G10_EmptyCreationLookupCannotHidePreviewOrCapacityReason(bool atCapacity, bool composition)
    {
        var parents = SetupExistingParents("pt-BR");
        if (composition) EnableComposition();
        var gate = new PayGateService(_habitRepo, _userRepo, _appConfig, _userDateService, _featureFlagService);
        _appConfig.GetAsync(AppConfigKeys.FreeMaxHabits, AppConstants.DefaultFreeMaxHabits, Arg.Any<CancellationToken>())
            .Returns(atCapacity ? 2 : 10);
        _habitRepo.CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => parents.Count(call.Arg<Expression<Func<Habit, bool>>>().Compile()));
        var mediator = Orbit.Tests.Shared.HabitToolCommandMediator.Create(_habitRepo, _tagRepo, _goalRepo,
            _userDateService, gate, _unitOfWork, users: _userRepo);
        var create = new CreateHabitTool(_userDateService, mediator);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(query, create);
        var blocks = UseProductionReadExecutor(query);
        var outcomes = new List<AgentExecuteOperationResponse>();
        UseCapacityReplayExecutor(create, outcomes);
        var reader = Orbit.Application.Tests.Chat.ReadBlockScopeTests.CreateExecutor(query, blocks);
        _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId == query.Name),
                Arg.Any<CancellationToken>())
            .Returns(call => reader.ExecuteAsync(call.Arg<AgentExecuteOperationRequest>(), call.Arg<CancellationToken>()));
        using var capture = GateTenPreviewCapture(atCapacity ? "edge-free-limit-en-r1" : "single-create-pt-r1");
        var calls = BrainDumpCalls(capture);
        SetupAiResponse(new AiResponse { ToolCalls = [calls[0]], ConversationContext = TestConversationContext });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ToolCalls = [calls[1]], ConversationContext = TestConversationContext }),
                Result.Success(new AiResponse { TextMessage = "Não consegui concluir isso. Tente novamente." }));

        var result = await handler.Handle(new(UserId, "Prepare a criação solicitada.",
            ClientContext: new(SupportsParts: true, SupportsHabitListCard: true, SupportsPendingOperationChanges: true,
                PartKinds: ["habitList"], Locale: "pt-BR")), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var operation = outcomes.Should().ContainSingle().Which.Operation;
        if (atCapacity)
        {
            operation.Status.Should().Be(AgentOperationStatus.Failed);
            operation.Payload.Should().BeOfType<Orbit.Application.Chat.Models.HabitCapacityFailure>();
            result.Value.PendingOperations.Should().BeNullOrEmpty();
            result.Value.AiMessage.Should().Be("Você chegou ao limite de 2 hábitos. Você pode adicionar um sub-hábito a um hábito existente.");
            result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>()
                .Which.Text.Should().Be(result.Value.AiMessage);
        }
        else
        {
            operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
            result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().ContainSingle()
                .Which.EntityName.Should().Be("Meditar");
            result.Value.AiMessage.Should().BeNullOrEmpty();
            result.Value.Parts.Should().BeEmpty();
        }
        result.Value.HabitList.Should().BeNull();
        result.Value.Parts!.OfType<ChatBlockPart>().Should().BeEmpty();
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", true, true)]
    [InlineData("pt-BR", true, true)]
    [InlineData("en", false, true)]
    [InlineData("pt-BR", false, true)]
    [InlineData("en", true, false)]
    [InlineData("pt-BR", true, false)]
    [InlineData("en", false, false)]
    [InlineData("pt-BR", false, false)]
    public async Task G10_CapacityRefusalKeepsIndependentHabitRead(string language, bool supportsParts, bool composition)
    {
        var parents = SetupExistingParents(language);
        if (composition) EnableComposition();
        var gate = new PayGateService(_habitRepo, _userRepo, _appConfig, _userDateService, _featureFlagService);
        _appConfig.GetAsync(AppConfigKeys.FreeMaxHabits, AppConstants.DefaultFreeMaxHabits, Arg.Any<CancellationToken>())
            .Returns(2);
        _habitRepo.CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => parents.Count(call.Arg<Expression<Func<Habit, bool>>>().Compile()));
        var mediator = Orbit.Tests.Shared.HabitToolCommandMediator.Create(_habitRepo, _tagRepo, _goalRepo,
            _userDateService, gate, _unitOfWork, users: _userRepo);
        var create = new CreateHabitTool(_userDateService, mediator);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(query, create);
        var blocks = UseProductionReadExecutor(query);
        var outcomes = new List<AgentExecuteOperationResponse>();
        UseCapacityReplayExecutor(create, outcomes);
        var reader = Orbit.Application.Tests.Chat.ReadBlockScopeTests.CreateExecutor(query, blocks);
        _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId == query.Name),
                Arg.Any<CancellationToken>())
            .Returns(call => reader.ExecuteAsync(call.Arg<AgentExecuteOperationRequest>(), call.Arg<CancellationToken>()));
        SetupAiResponse(ToolResponse(query.Name, "habits", """{"include_metrics":false,"orbit_read_complete":false}"""));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ToolResponse(create.Name, "create",
                    """{"title":"Stretch","frequency_unit":"Day","frequency_quantity":1,"orbit_write_complete":true}""")),
                Result.Success(new AiResponse { TextMessage = "Try again." }));

        var result = await handler.Handle(new(UserId, "Show my existing habits and create Stretch.",
            ClientContext: new(SupportsParts: supportsParts, SupportsHabitListCard: true,
                PartKinds: ["habitList"], Locale: language)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var refusal = language == "pt-BR"
            ? "Você chegou ao limite de 2 hábitos. Você pode adicionar um sub-hábito a um hábito existente."
            : "You've reached the 2 habit limit. You can add a sub-habit to an existing habit.";
        var read = result.Value.Operations.Should().ContainSingle(operation => operation.OperationId == query.Name)
            .Which.ReadBlock!.Block.Should().BeOfType<HabitListCard>().Subject;
        read.Items.Select(item => item.Id).Should().BeEquivalentTo(parents.Select(habit => habit.Id.ToString()));
        if (supportsParts)
        {
            result.Value.Parts.Should().HaveCount(2);
            var block = result.Value.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Which;
            block.Kind.Should().Be("habitList");
            block.Block.Should().BeOfType<HabitListCard>().Which.Items.Select(item => item.Id)
                .Should().BeEquivalentTo(parents.Select(habit => habit.Id.ToString()));
            result.Value.Parts.OfType<ChatTextPart>().Should().ContainSingle().Which.Text.Should().Be(refusal);
            result.Value.AiMessage.Should().Be(refusal);
        }
        else
        {
            result.Value.Parts.Should().BeNull();
            result.Value.AiMessage.Should().Contain("Post Lunch").And.Contain("Before Bed").And.Contain(refusal)
                .And.NotContain("Try again.");
        }
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        result.Value.Actions.Should().BeEmpty();
        outcomes.Should().ContainSingle().Which.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        _pendingOperationStore.DidNotReceiveWithAnyArgs().Create(default, default!, default!, default!, default!, default!, default);
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    private static JsonDocument GateTenPreviewCapture(string name) => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "GateTenPreviews", name + ".json")));

    [Fact]
    public async Task G10_DuplicateTargetProducesOneChoiceAndNoWrite()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var habits = new[] { CreateHabit("Caminhada"), CreateHabit("Caminhada") };
        SetupSelectionHabits(habits);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(query, RealAskTool());
        UseProductionReadExecutor(query);
        using var capture = GateTenPreviewCapture("ambiguity-pt-r2");
        var calls = BrainDumpCalls(capture);
        var ask = JsonNode.Parse(calls[1].Args.GetRawText())!.AsObject();
        ask["missing_input"] = "record_selection";
        for (var index = 0; index < habits.Length; index++)
            ask["options"]![index]!["id"] = habits[index].Id.ToString();
        SetupAiResponse(new AiResponse { ToolCalls = [calls[0]], ConversationContext = TestConversationContext });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ToolCalls = [calls[1] with { Args = JsonSerializer.SerializeToElement(ask) }],
                ConversationContext = TestConversationContext
            }));

        var result = await handler.Handle(new(UserId, "Marca a caminhada como feita.",
            ClientContext: new(SupportsParts: true, PartKinds: ["choice"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Kind.Should().Be("choice");
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        result.Value.Actions.Should().BeEmpty();
        await _operationExecutor.Received(1).ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(call =>
            call.OperationId == "query_habits"), Arg.Any<CancellationToken>());
        await _operationExecutor.DidNotReceive().ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(call =>
            call.OperationId != "query_habits"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task G10_ChecklistLookupContinuesToAppendTheSameStepUnchecked()
    {
        SetupUserAndPayGate();
        EnableComposition();
        using var failed = GateTenPreviewCapture("composition-one-small-step-save-checklist-pt-BR-r3");
        using var passed = GateTenPreviewCapture("composition-one-small-step-save-checklist-pt-BR-r1");
        var fixture = failed.RootElement.GetProperty("fixtureHabits")[0];
        var habit = CreateHabit(fixture.GetProperty("title").GetString()!);
        var existing = fixture.GetProperty("checklistItems").EnumerateArray()
            .Select(item => new ChecklistItem(item.GetProperty("text").GetString()!, item.GetProperty("isChecked").GetBoolean())).ToArray();
        habit.UpdateChecklist(existing);
        SetupGateTenHabits(habit);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var write = new UpdateChecklistTool(_mediator, _habitRepo);
        var handler = CreateHandler(query, write);
        var held = HoldBrainDumpWithReads(write, query);
        var lookup = BrainDumpCalls(failed).Single();
        var append = BrainDumpCalls(passed).Single();
        append = append with
        {
            Args = JsonSerializer.Deserialize<JsonElement>(append.Args.GetRawText()
            .Replace("[redacted]", habit.Id.ToString(), StringComparison.Ordinal))
        };
        SetupGateTenRounds(lookup, append);

        var result = await handler.Handle(new(UserId, "Quero adicionar Pegar um copo ao checklist de Beber água.",
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, PartKinds: ["oneSmallStep"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        held.Should().ContainSingle();
        var preview = result.Value.PendingOperations.Should().ContainSingle().Which;
        var items = preview.Items.Should().ContainSingle().Which.Fields
            .Should().ContainSingle(field => field.Field == "checklist_items").Which.ProposedValue!.Value;
        items.EnumerateArray().Select(item => new ChecklistItem(item.GetProperty("text").GetString()!,
                item.GetProperty("is_checked").GetBoolean()))
            .Should().Equal(existing.Append(new ChecklistItem("Pegar um copo", false)));
        habit.ChecklistItems.Should().Equal(existing);
        result.Value.Parts.Should().BeEmpty();
        result.Value.AiMessage.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task G10_BrainDumpKeepsExplicitTasksAndTheReturnedIdeaInOnePreview()
    {
        SetupUserAndPayGate();
        EnableComposition();
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 10, 30));
        using var capture = GateTenPreviewCapture("brain-dump-ideas-pt-BR-r1");
        var suggestion = capture.RootElement.GetProperty("successfulTools").EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "suggest_habits")
            .GetProperty("result").GetProperty("drafts")[0];
        var draft = suggestion.Deserialize<HabitSuggestionDraft>(new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } })! with
        { Id = Guid.NewGuid() };
        var generator = Substitute.For<IHabitDraftGenerator>();
        generator.GenerateAsync(Arg.Any<HabitSuggestionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<HabitSuggestionDraft>>([draft]));
        var suggestions = new SuggestHabitDraftsQueryHandler(new HabitSuggestionService(_habitRepo, _goalRepo, _userDateService, generator));
        _mediator.Send(Arg.Any<SuggestHabitDraftsQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => suggestions.Handle(call.Arg<SuggestHabitDraftsQuery>(), call.Arg<CancellationToken>()));
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var suggest = new SuggestHabitsTool(_mediator);
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(query, suggest, bulk);
        var held = HoldBrainDumpWithReads(bulk, query, suggest);
        SetupGateTenRounds(BrainDumpCalls(capture));

        var result = await handler.Handle(new(UserId, "Organiza minhas tarefas e a ideia em uma prévia para aprovar.",
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, Locale: "pt-BR")), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        held.Should().ContainSingle();
        var items = result.Value.PendingOperations.Should().ContainSingle().Which.Items!;
        items.Select(item => item.HabitType).Should().Equal("Task", "Habit", "Habit");
        items[0].EntityName.Should().Contain("dentista");
        items[0].Fields.Should().Contain(field => field.Field == "due_date" && field.NewValue == "2026-10-31");
        items[1].EntityName.Should().Be("Alongar");
        items[1].Fields.Should().Contain(field => field.Field == "due_time" && field.NewValue == "08:00");
        items[2].EntityName.Should().Be(draft.Title);
        items[2].Fields.Should().Contain(field => field.Field == "due_time" && field.NewValue == draft.DueTime.ToString("HH:mm"));
        result.Value.Parts.Should().BeEmpty();
        result.Value.AiMessage.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task G10_RestartReadsAllSixTargetsBeforePreparingTheirDates()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var today = new DateOnly(2026, 10, 30);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(today);
        using var capture = GateTenPreviewCapture("composition-restart-approve-pt-BR-r2");
        var source = capture.RootElement.GetProperty("successfulTools")[0].GetProperty("result").GetProperty("items");
        var habits = source.EnumerateArray().Select((item, index) => Habit.Create(new HabitCreateParams(
            HabitSchedule.Recurring, UserId, item.GetProperty("title").GetString()!, FrequencyUnit.Day, 1,
            index < 4 ? today.AddDays(-2) : today)).Value).ToArray();
        SetupGateTenHabits(habits);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var write = new BulkRescheduleHabitsTool(_mediator);
        var handler = CreateHandler(query, write);
        var held = HoldBrainDumpWithReads(write, query);
        var calls = BrainDumpCalls(capture);
        var plan = JsonNode.Parse(calls[1].Args.GetRawText())!.AsObject();
        for (var index = 0; index < habits.Length; index++)
            plan["revised_items"]![index]!["habit_id"] = habits[index].Id.ToString();
        SetupAiResponse(new AiResponse { ToolCalls = [calls[0]], ConversationContext = TestConversationContext });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var rows = call.Arg<IReadOnlyList<AiToolCallResult>>().Single().Payload.Should().BeOfType<HabitListCard>().Subject;
                rows.Items.Select(item => item.Id).Should().BeEquivalentTo(habits.Select(habit => habit.Id.ToString()));
                held.Should().BeEmpty();
                return Result.Success(new AiResponse
                {
                    ToolCalls = [calls[1] with { Args = JsonSerializer.SerializeToElement(plan) }],
                    ConversationContext = TestConversationContext
                });
            });

        var result = await handler.Handle(new(UserId, "Me ajude a recomeçar.",
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, SupportsHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        held.Should().ContainSingle();
        var items = result.Value.PendingOperations.Should().ContainSingle().Which.Items!;
        items.Select(item => item.EntityId).Should().BeEquivalentTo(habits.Select(habit => (Guid?)habit.Id));
        var dates = items.Select(item => DateOnly.Parse(item.Fields.Single(field => field.Field == "due_date").NewValue!)).ToArray();
        dates.Count(date => date == today).Should().BeInRange(1, habits.Length - 1);
        dates.Should().OnlyContain(date => date >= today && date <= today.AddDays(7));
        result.Value.Parts.Should().BeEmpty();
        habits.Take(4).Should().OnlyContain(habit => habit.NextDueDate == today.AddDays(-2));
    }

    private void SetupGateTenRounds(params AiToolCall[] calls)
    {
        SetupAiResponse(new AiResponse { ToolCalls = [calls[0]], ConversationContext = TestConversationContext });
        var remaining = new Queue<AiToolCall>(calls.Skip(1));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Result.Success(new AiResponse { ToolCalls = [remaining.Dequeue()], ConversationContext = TestConversationContext }));
    }

    private void SetupGateTenHabits(params Habit[] habits)
    {
        SetupSelectionHabits(habits);
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToArray());
    }
}
