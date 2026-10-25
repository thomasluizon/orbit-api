using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Fact]
    public async Task Replay_GoalWithReadingLink_ResolvesAndCreatesLinkedGoal()
    {
        var habits = SetupResolutionAccount();
        var goal = new CreateGoalTool(Orbit.Tests.Shared.GoalToolCommandMediator.Create(_goalRepo, _unitOfWork, habits: _habitRepo));
        var handler = CreateHandler(goal);
        var held = HoldReplayWrites(goal);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ReplayCall("create_goal", """{"title":"Ler 12 livros","target_value":12,"unit":"livros","deadline":"2026-12-31","habit_ids":["Ler 10 minutos"]}"""));
        ReplyToReplay("Revise a meta antes de aprovar.");

        var result = await handler.Handle(new(UserId, "Crie uma meta de ler 12 livros até dezembro e ligue ao hábito de leitura."), default);

        result.Value.PendingOperations.Should().ContainSingle();
        held.Should().ContainSingle().Which.Arguments.GetProperty("habit_ids")[0].GetString().Should().Be(habits[1].Id.ToString());
        (await goal.ExecuteAsync(held[0].Arguments, UserId, default)).Success.Should().BeTrue();
        await _goalRepo.Received().AddAsync(Arg.Is<Goal>(value => value.Habits.Single().Id == habits[1].Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Replay_LogsMatchingHabitsOnSixDates_OnePreviewAndMissingNameQuestion()
    {
        var habits = SetupResolutionAccount();
        var log = new BulkLogHabitsTool(_mediator, _habitRepo, _userDateService,
            new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(), Substitute.For<IIdempotencyStore>(), _unitOfWork));
        var handler = CreateHandler(log);
        var held = HoldReplayWrites(log);
        const string arguments = """{"habit_ids":["água","Ler 10 minutos","meditação"],"dates":["2026-03-28","2026-03-29","2026-03-30","2026-03-31","2026-04-01","2026-04-03"]}""";
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ToolCalls = [new("bulk_log_habits", "log1", ParseArguments(arguments)), new("bulk_log_habits", "log2", ParseArguments(arguments))], ConversationContext = new() }));
        ReplyToReplay("Revise os registros.");

        var result = await handler.Handle(new(UserId, "Registre água, leitura e meditação nos últimos 7 dias, menos ontem."), default);

        result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().HaveCount(12);
        result.Value.AiMessage.Should().Contain("meditação").And.Contain("?");
        held.Should().ContainSingle();
        var items = held[0].Arguments.GetProperty("revised_items").EnumerateArray().ToArray();
        items.Select(item => item.GetProperty("habit_id").GetString()).Distinct().Should().BeEquivalentTo(habits.Take(2).Select(habit => habit.Id.ToString()));
        items.Select(item => item.GetProperty("date").GetString()).Distinct().Should().HaveCount(6);
    }

    [Fact]
    public async Task Replay_BreakdownUsesExistingGym_ApprovalCreatesChildren()
    {
        var habits = SetupResolutionAccount();
        var bulk = new BulkCreateHabitsTool(_mediator);
        var single = new CreateHabitTool(_userDateService, Orbit.Tests.Shared.HabitToolCommandMediator.Create(_habitRepo, _tagRepo, _goalRepo, _userDateService, _payGate, _unitOfWork));
        var handler = CreateHandler(bulk, single);
        var held = HoldReplayWrites(bulk, single);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ReplayCall("create_habit", """{"title":"academia","sub_habits":[{"title":"Aquecimento"},{"title":"Treino"},{"title":"Alongamento"}]}"""));
        ReplyToReplay("Revise a divisão.");
        _mediator.Send(Arg.Any<CreateSubHabitCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success(Guid.NewGuid()));

        var result = await handler.Handle(new(UserId, "Divida a academia em aquecimento, treino e alongamento."), default);

        result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().HaveCount(3);
        held.Should().ContainSingle().Which.Arguments.GetProperty("parent_habit_id").GetString().Should().Be(habits[2].Id.ToString());
        (await bulk.ExecuteAsync(held[0].Arguments, UserId, default)).Success.Should().BeTrue();
        await _mediator.Received(3).Send(Arg.Is<CreateSubHabitCommand>(command => command.ParentHabitId == habits[2].Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Replay_HealthTag_DoesNotRecreateExistingHabits()
    {
        var habits = SetupResolutionAccount();
        var bulk = new BulkCreateHabitsTool(_mediator);
        var tags = new AssignTagsTool(_habitRepo, _tagRepo, _unitOfWork);
        var handler = CreateHandler(bulk, tags);
        var held = HoldReplayWrites(bulk, tags);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ReplayCall("bulk_create_habits", """{"habits":[{"title":"Água"},{"title":"Academia"},{"title":"Meditação"}]}"""));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ReplayCall("assign_tags", """{"habit_id":"academia","tag_names":["Saúde"]}"""), Result.Success(new AiResponse { TextMessage = "Revise a tag." }));

        var result = await handler.Handle(new(UserId, "Crie uma tag Saúde e atribua à academia."), default);

        result.Value.PendingOperations.Should().ContainSingle();
        held.Should().ContainSingle().Which.OperationId.Should().Be("assign_tags");
        held[0].Arguments.GetProperty("habit_id").GetString().Should().Be(habits[2].Id.ToString());
        held.Should().OnlyContain(operation => operation.OperationId != "bulk_create_habits"
            && operation.OperationId != "create_habit");
        await _aiIntentService.Received().ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(values => values.Any(value => value.Name == "bulk_create_habits"
                && JsonSerializer.SerializeToElement(value.Payload).GetProperty("status").GetString() == "no_new_preview")),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Replay_PreviewReturnsNumericFactsToModel()
    {
        SetupResolutionAccount();
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(bulk);
        HoldReplayWrites(bulk);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ReplayCall("bulk_create_habits", """{"habits":[{"title":"Novo"}]}"""));
        ReplyToReplay("Revise 1 hábito.");

        var result = await handler.Handle(new(UserId, "Crie um hábito Novo."), default);

        result.Value.GroundingFacts.Facts.Should().Contain(fact => fact.Kind == "Number" && fact.Value == "1");
        Orbit.Application.Chat.Grounding.GroundednessChecker.Check(result.Value.AiMessage, result.Value.GroundingFacts).Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"habit_ids":["meditação"]}""")]
    [InlineData("""{"filter":{"search":"meditação"}}""")]
    public async Task Replay_UnresolvedNames_NoEmptyPreviewAndOneQuestion(string arguments)
    {
        SetupResolutionAccount();
        var log = new BulkLogHabitsTool(_mediator, _habitRepo, _userDateService,
            new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(), Substitute.For<IIdempotencyStore>(), _unitOfWork));
        var handler = CreateHandler(log);
        var held = HoldReplayWrites(log);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ReplayCall("bulk_log_habits", arguments));
        ReplyToReplay("Escolha um hábito.");

        var result = await handler.Handle(new(UserId, "Registre meditação."), default);

        held.Should().BeEmpty();
        result.Value.PendingOperations.Should().BeEmpty();
        result.Value.AiMessage.Should().Contain("meditação").And.Contain("?");
    }

    [Theory]
    [InlineData("Novo", "NOVO")]
    [InlineData("Praticar yoga", "PRATICAR YOGA")]
    public async Task Replay_PreviousCreateProposal_IsRefusedAcrossTurns(string previousName, string proposedName)
    {
        SetupResolutionAccount();
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(bulk);
        var held = HoldReplayWrites(bulk);
        _pendingOperationStore.GetPendingHabitCreations(UserId).Returns([new PendingHabitCreation(previousName)]);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ReplayCall("bulk_create_habits", JsonSerializer.Serialize(new { habits = new[] { new { title = proposedName, description = "A changed proposal" } } })));
        ReplyToReplay("A prévia anterior continua disponível.");

        var result = await handler.Handle(new(UserId, "Crie Novo de novo.", History: [new ChatHistoryMessage("user", "Crie os hábitos da prévia anterior.")]), default);

        result.Value.PendingOperations.Should().BeEmpty();
        held.Should().BeEmpty();
    }

    [Fact]
    public async Task Replay_TagMatchingHabits_UsesOneApprovalForTheIntent()
    {
        var habits = SetupResolutionAccount();
        var tags = new AssignTagsTool(_habitRepo, _tagRepo, _unitOfWork);
        var handler = CreateHandler(tags);
        var held = HoldReplayWrites(tags);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ToolCalls = [new("assign_tags", "tag1", ParseArguments("""{"habit_id":"água","tag_names":["Saúde"]}""")),
                    new("assign_tags", "tag2", ParseArguments("""{"habit_id":"academia","tag_names":["Saúde"]}"""))],
                ConversationContext = new()
            }));
        ReplyToReplay("Revise a tag.");

        var result = await handler.Handle(new(UserId, "Atribua Saúde a água e academia."), default);

        result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().HaveCount(2);
        held.Should().ContainSingle();
        held[0].Arguments.GetProperty("habit_ids").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Replay_LogPreview_GroundsHabitAndDateCounts()
    {
        var habits = SetupResolutionAccount();
        var log = new BulkLogHabitsTool(_mediator, _habitRepo, _userDateService,
            new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(), Substitute.For<IIdempotencyStore>(), _unitOfWork));
        var handler = CreateHandler(log);
        HoldReplayWrites(log);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ReplayCall("bulk_log_habits", """{"habit_ids":["água","Ler 10 minutos"],"dates":["2026-03-28","2026-03-29","2026-03-30","2026-03-31","2026-04-01","2026-04-03"]}"""));
        ReplyToReplay("Revise 12 registros de 2 hábitos em 6 datas.");

        var result = await handler.Handle(new(UserId, "Registre água e leitura nos últimos sete dias, menos ontem."), default);

        Orbit.Application.Chat.Grounding.GroundednessChecker.Check(result.Value.AiMessage, result.Value.GroundingFacts,
            habits.Select(habit => habit.Title), "pt-BR").Should().BeEmpty();
    }

    [Fact]
    public async Task Replay_SixSeparateDateCalls_ProducesOneApproval()
    {
        SetupResolutionAccount();
        var log = new BulkLogHabitsTool(_mediator, _habitRepo, _userDateService,
            new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(), Substitute.For<IIdempotencyStore>(), _unitOfWork));
        var handler = CreateHandler(log);
        var held = HoldReplayWrites(log);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ToolCalls = Enumerable.Range(0, 7).Where(offset => offset != 1).Select(offset =>
                    new AiToolCall("bulk_log_habits", "date" + offset, JsonSerializer.SerializeToElement(new
                    {
                        habit_ids = new[] { "água", "Ler 10 minutos" },
                        date = Today.AddDays(-offset).ToString("yyyy-MM-dd")
                    }))).ToArray(),
                ConversationContext = new()
            }));
        ReplyToReplay("Revise os registros.");

        var result = await handler.Handle(new(UserId, "Registre água e leitura nos últimos sete dias, menos ontem."), default);

        result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().HaveCount(12);
        held.Should().ContainSingle();
    }

    [Fact]
    public async Task Replay_BreakdownWithoutParent_AsksInsteadOfCreatingRoot()
    {
        SetupResolutionAccount();
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(bulk);
        var held = HoldReplayWrites(bulk);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ReplayCall("bulk_create_habits", """{"habits":[{"title":"Yoga","sub_habits":[{"title":"Aquecimento"}]}]}"""));
        ReplyToReplay("Escolha o hábito.");

        var result = await handler.Handle(new(UserId, "Divida Yoga em aquecimento e prática."), default);

        held.Should().BeEmpty();
        result.Value.PendingOperations.Should().BeEmpty();
        result.Value.AiMessage.Should().Contain("Yoga").And.Contain("?");
    }

    [Fact]
    public async Task Replay_DuplicateNamesInCreateBatch_AreRefused()
    {
        SetupResolutionAccount();
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(bulk);
        var held = HoldReplayWrites(bulk);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ReplayCall("bulk_create_habits", """{"habits":[{"title":"Meditação"},{"title":"MEDITACAO"}]}"""));
        ReplyToReplay("Revise a prévia.");

        var result = await handler.Handle(new(UserId, "Crie meditação."), default);

        result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().ContainSingle();
        held.Should().ContainSingle();
    }

    [Theory]
    [InlineData("Crie Rotina com aquecimento.", "Rotina", "Rotina da manhã")]
    [InlineData("Crie Rotina com sub-hábitos de aquecimento.", "Rotina", "Rotina da manhã")]
    [InlineData("Crie o hábito Correr com a tag Saúde.", "Correr", "Correr 5 km")]
    [InlineData("Cria um hábito Correr com a tag Saúde.", "Correr", "Correr 5 km")]
    public async Task RoundThree_ExplicitCreate_DoesNotBecomeExistingParent(string message, string title, string existing)
    {
        SetupResolutionAccount();
        var matching = new[] { CreateHabit(existing) };
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns(matching);
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>()).Returns(matching);
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(bulk);
        var held = HoldReplayWrites(bulk);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ReplayCall("bulk_create_habits", JsonSerializer.Serialize(new { habits = new[] { new { title, sub_habits = new[] { new { title = "Aquecimento" } } } } })));
        ReplyToReplay("Revise a prévia.");

        var result = await handler.Handle(new(UserId, message), default);

        held.Should().ContainSingle();
        held[0].Arguments.TryGetProperty("parent_habit_id", out _).Should().BeFalse();
        held[0].Arguments.GetProperty("habits")[0].GetProperty("title").GetString().Should().Be(title);
    }

    [Fact]
    public async Task RoundThree_AmbiguousHabit_ListsCandidatesAndDoesNotWrite()
    {
        SetupResolutionAccount();
        var matching = new[] { CreateHabit("Água de manhã"), CreateHabit("Água de noite") };
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns(matching);
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>()).Returns(matching);
        var tags = new AssignTagsTool(_habitRepo, _tagRepo, _unitOfWork);
        var handler = CreateHandler(tags);
        var held = HoldReplayWrites(tags);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ReplayCall("assign_tags", """{"habit_id":"água","tag_names":["Saúde"]}"""));
        ReplyToReplay("Qual hábito?");

        var result = await handler.Handle(new(UserId, "Atribua Saúde a água."), default);

        held.Should().BeEmpty();
        result.Value.AiMessage.Should().Contain("Água de manhã").And.Contain("Água de noite").And.NotContain("Não encontrei");
    }

    [Theory]
    [InlineData("Prepare uma nova prévia para criar Novo.")]
    [InlineData("Prepare uma nova.")]
    [InlineData("Prepare a new one.")]
    public async Task RoundThree_PendingDuplicate_AsksAndNextExplicitRequestCanReplaceIt(string replacementRequest)
    {
        SetupResolutionAccount();
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(bulk);
        var held = HoldReplayWrites(bulk);
        _pendingOperationStore.GetPendingHabitCreations(UserId).Returns([new PendingHabitCreation("Novo")]);
        _pendingOperationStore.SupersedeHabitCreation(UserId, "Novo", null).Returns(true);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ReplayCall("bulk_create_habits", """{"habits":[{"title":"NOVO"}]}"""));
        ReplyToReplay("Revise a prévia.");

        var first = await handler.Handle(new(UserId, "Crie Novo.", History: [new ChatHistoryMessage("user", "Crie os hábitos da prévia anterior.")]), default);

        held.Should().BeEmpty();
        first.Value.AiMessage.Should().Contain("Novo").And.Contain("prévia").And.Contain("Aprove");
        var next = await handler.Handle(new(UserId, replacementRequest,
            History: [new ChatHistoryMessage("assistant", first.Value.AiMessage!)]), default);
        held.Should().ContainSingle();
        next.Value.PendingOperations.Should().ContainSingle();
    }

    [Fact]
    public async Task RoundThree_ExistingExactDuplicate_ReusesItsIdentityInPreview()
    {
        SetupResolutionAccount();
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(bulk);
        HoldReplayWrites(bulk);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ReplayCall("bulk_create_habits", """{"habits":[{"title":"ACADEMIA"}]}"""));
        ReplyToReplay("Tudo certo.");

        var result = await handler.Handle(new(UserId, "Crie Academia."), default);

        result.Value.PendingOperations.Should().ContainSingle();
        result.Value.AiMessage.Should().Contain("Academia").And.Contain("aprovar");
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"habit_ids\":[7],\"tag_names\":[\"Saúde\"]}")]
    public async Task RoundThree_MalformedModelArguments_ReturnToolError(string arguments)
    {
        SetupResolutionAccount();
        var tags = new AssignTagsTool(_habitRepo, _tagRepo, _unitOfWork);
        var handler = CreateHandler(tags);
        var held = HoldReplayWrites(tags);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ReplayCall("assign_tags", arguments));
        ReplyToReplay("Tente novamente.");

        var result = await handler.Handle(new(UserId, "Atribua Saúde à academia."), default);

        result.IsSuccess.Should().BeTrue();
        held.Should().BeEmpty();
        await _aiIntentService.Received().ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(values => values.Any(value => !value.Success)),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RoundThree_LogCoalescing_UsesTurnSnapshotAndReturnsCompactFacts()
    {
        SetupResolutionAccount();
        var log = new BulkLogHabitsTool(_mediator, _habitRepo, _userDateService,
            new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(), Substitute.For<IIdempotencyStore>(), _unitOfWork));
        var handler = CreateHandler(log);
        HoldReplayWrites(log);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ToolCalls = Enumerable.Range(1, 6).Select(day => new AiToolCall("bulk_log_habits", "log" + day,
                    JsonSerializer.SerializeToElement(new { habit_ids = new[] { "água" }, date = $"2026-04-0{day}" }))).ToArray(),
                ConversationContext = new()
            }));
        ReplyToReplay("Revise os registros.");

        var result = await handler.Handle(new(UserId, "Registre água."), default);

        result.Value.PendingOperations.Should().ContainSingle();
        await _habitRepo.Received(2).FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>());
        await _aiIntentService.Received().ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(values => values.Where(value => value.Payload != null)
                .All(value => !JsonSerializer.SerializeToElement(value.Payload).EnumerateObject().Any(property => property.Name == "items"))),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        var returned = _aiIntentService.ReceivedCalls().Single(call => call.GetMethodInfo().Name == "ContinueWithToolResultsAsync")
            .GetArguments()[1].Should().BeAssignableTo<IReadOnlyList<AiToolCallResult>>().Subject;
        returned.Count(value => JsonSerializer.SerializeToElement(value.Payload).GetProperty("status").GetString() == "awaiting_confirmation")
            .Should().Be(1);
        var merged = returned.Where(value => JsonSerializer.SerializeToElement(value.Payload).GetProperty("status").GetString() == "merged_into_preview").ToArray();
        merged.Should().HaveCount(5).And.OnlyContain(value => value.Success && value.Error == null);
        merged.Should().OnlyContain(value => JsonSerializer.SerializeToElement(value.Payload).GetProperty("instruction").GetString()!
            .Contains("Do not repeat", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RoundFour_MalformedCreateAfterDuplicate_DoesNotReachExecutor()
    {
        SetupResolutionAccount();
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(bulk);
        var held = HoldReplayWrites(bulk);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ReplayCall("bulk_create_habits", """{"habits":[{"title":"Academia"},{"title":42}]}"""));
        ReplyToReplay("Tente novamente.");

        var result = await handler.Handle(new(UserId, "Crie Academia e outro hábito."), default);

        held.Should().BeEmpty();
        result.Value.PendingOperations.Should().BeEmpty();
        result.Value.Actions.Should().Contain(action => action.Status == ActionStatus.Failed
            && action.MessageKey == "chat.toolResult.toolFailure" && action.Error == PortugueseToolFailureMessage);
        await _aiIntentService.Received().ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(values => values.Any(value => !value.Success
                && value.Error != null && value.Error.Contains("title", StringComparison.Ordinal))),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("pt-BR", "Você já tem uma prévia aguardando para criar Novo. Aprove a prévia ou me peça para preparar uma nova.")]
    [InlineData("en", "You already have a preview waiting to create Novo. Approve it, or ask me to prepare a new one.")]
    public async Task RoundFour_PendingDuplicate_UsesRequestedCopy(string language, string expected)
    {
        SetupResolutionAccount();
        var user = User.Create("Test", "copy@example.com").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(bulk);
        HoldReplayWrites(bulk);
        _pendingOperationStore.GetPendingHabitCreations(UserId).Returns([new PendingHabitCreation("Novo")]);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ReplayCall("bulk_create_habits", """{"habits":[{"title":"Novo"}]}"""));
        ReplyToReplay("Review.");

        var result = await handler.Handle(new(UserId, "Crie Novo.", History: [new ChatHistoryMessage("user", "Crie os hábitos da prévia anterior.")]), default);

        result.Value.AiMessage.Should().Be(expected);
        result.Value.PendingOperations.Should().BeEmpty();
    }

    [Fact]
    public async Task RoundFour_Replacement_UsesStoredProposalWithUnrelatedReplyCopy()
    {
        SetupResolutionAccount();
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(bulk);
        var held = HoldReplayWrites(bulk);
        _pendingOperationStore.GetPendingHabitCreations(UserId).Returns([new PendingHabitCreation("Novo")]);
        _pendingOperationStore.SupersedeHabitCreation(UserId, "Novo", null).Returns(true);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ReplayCall("bulk_create_habits", """{"habits":[{"title":"Novo"}]}"""));
        ReplyToReplay("Revise a prévia.");

        var result = await handler.Handle(new(UserId, "Crie Novo de novo.",
            History: [new ChatHistoryMessage("assistant", "Há uma proposta aguardando sua aprovação.")]), default);

        _pendingOperationStore.Received().SupersedeHabitCreation(UserId, "Novo", null);
        held.Should().ContainSingle();
        result.Value.PendingOperations.Should().ContainSingle();
    }

    [Theory]
    [InlineData("pt-BR", "A prévia para criar Treino também inclui outros hábitos. Quais itens você quer manter na prévia?")]
    [InlineData("en", "The preview to create Treino also includes other habits. Which items would you like to keep in the preview?")]
    public async Task RoundFour_MultiItemReplacement_AsksBeforeSupersedingBatch(string language, string expected)
    {
        SetupResolutionAccount();
        var user = User.Create("Test", "copy@example.com").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(bulk);
        var held = HoldReplayWrites(bulk);
        _pendingOperationStore.GetPendingHabitCreations(UserId).Returns([
            new PendingHabitCreation("Aquecimento"), new PendingHabitCreation("Treino"), new PendingHabitCreation("Alongamento")]);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ReplayCall("bulk_create_habits", """{"habits":[{"title":"Treino"}]}"""));
        ReplyToReplay("Review.");

        var result = await handler.Handle(new(UserId, "Prepare uma nova prévia para Treino.", History: [new ChatHistoryMessage("user", "Crie os hábitos da prévia anterior.")]), default);

        held.Should().BeEmpty();
        result.Value.PendingOperations.Should().BeEmpty();
        result.Value.AiMessage.Should().Be(expected);
    }

    [Theory]
    [InlineData("pt-BR", false, "Não encontrei este hábito: Meditação. Qual hábito você quer usar?")]
    [InlineData("pt-BR", true, "Não encontrei estes hábitos: Meditação, Yoga. Quais hábitos você quer usar?")]
    [InlineData("en", false, "I couldn't find this habit: Meditação. Which habit would you like to use?")]
    [InlineData("en", true, "I couldn't find these habits: Meditação, Yoga. Which habits would you like to use?")]
    public async Task RoundFour_MissingNames_UsesMatchingNumberInQuestion(string language, bool multiple, string expected)
    {
        SetupResolutionAccount();
        var user = User.Create("Test", "copy@example.com").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        var tags = new AssignTagsTool(_habitRepo, _tagRepo, _unitOfWork);
        var handler = CreateHandler(tags);
        var held = HoldReplayWrites(tags);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ReplayCall("assign_tags", JsonSerializer.Serialize(new
            {
                habit_ids = multiple ? new[] { "Meditação", "Yoga" } : new[] { "Meditação" },
                tag_names = new[] { "Saúde" }
            })));
        ReplyToReplay("Choose.");

        var result = await handler.Handle(new(UserId, "Atribua Saúde aos hábitos."), default);

        result.Value.AiMessage.Should().Be(expected);
        result.Value.PendingOperations.Should().BeEmpty();
        held.Should().BeEmpty();
    }

    [Fact]
    public async Task RoundThree_ExcessiveLogDates_ReturnClearError()
    {
        SetupResolutionAccount();
        var log = new BulkLogHabitsTool(_mediator, _habitRepo, _userDateService,
            new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(), Substitute.For<IIdempotencyStore>(), _unitOfWork));
        var arguments = JsonSerializer.SerializeToElement(new
        {
            habit_ids = new[] { Guid.NewGuid().ToString() },
            dates = Enumerable.Range(0, 101).Select(offset => Today.AddDays(-offset).ToString("yyyy-MM-dd")).ToArray()
        });

        var result = await log.CheckArgumentsAsync(arguments, UserId, default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("100");
        await _habitRepo.DidNotReceiveWithAnyArgs().FindAsync(default!, default(Func<IQueryable<Habit>, IQueryable<Habit>>), default);
    }

    [Fact]
    public async Task ExplicitChildrenUnderNamedParent_UsesExistingParent()
    {
        var habits = SetupResolutionAccount();
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(bulk);
        var held = HoldReplayWrites(bulk);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ReplayCall("bulk_create_habits", """{"parent_habit_id":"Academia","habits":[{"title":"Aquecimento"}]}"""));
        ReplyToReplay("Revise a prévia.");

        var result = await handler.Handle(new(UserId, "Crie sub-hábitos de aquecimento no hábito Academia."), default);

        result.Value.PendingOperations.Should().ContainSingle();
        held.Should().ContainSingle().Which.Arguments.GetProperty("parent_habit_id").GetString().Should().Be(habits[2].Id.ToString());
    }

    [Fact]
    public async Task Replay_LogSelectionRetainsCompletedHabitsFromTurnSnapshot()
    {
        var habits = SetupResolutionAccount().Append(CreateHabit("Concluído", true)).ToArray();
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        var log = new BulkLogHabitsTool(_mediator, _habitRepo, _userDateService,
            new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(), Substitute.For<IIdempotencyStore>(), _unitOfWork));
        var handler = CreateHandler(log);
        var held = HoldReplayWrites(log);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ReplayCall("bulk_log_habits", JsonSerializer.Serialize(new { habit_ids = new[] { habits[^1].Id }, date = Today.ToString("yyyy-MM-dd") })));
        ReplyToReplay("Revise os registros.");

        var result = await handler.Handle(new(UserId, "Registre Concluído."), default);

        result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().ContainSingle();
        held.Should().ContainSingle();
    }

    private Habit[] SetupResolutionAccount()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetLanguage("pt-BR");
        SetupUserAndPayGate(user);
        var habits = new[] { CreateHabit("Beber água"), CreateHabit("Ler 10 minutos"), CreateHabit("Academia") };
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        _habitRepo.FindTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        return habits;
    }

    private List<AgentExecuteOperationRequest> HoldReplayWrites(params IAiTool[] tools)
    {
        var registry = new AiToolRegistry(tools);
        var previewer = new PendingOperationChangePreviewer(_habitRepo, _goalRepo, _tagRepo, _userDateService, registry);
        var held = new List<AgentExecuteOperationRequest>();
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var request = call.Arg<AgentExecuteOperationRequest>();
            held.Add(request);
            var preview = (await previewer.PreviewAsync(UserId, request.OperationId, request.Arguments)).Value;
            var pending = new PendingAgentOperation(Guid.NewGuid(), request.OperationId, request.OperationId, request.OperationId,
                AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation, DateTime.UtcNow.AddMinutes(10),
                Changes: preview?.Changes, ChangeTargetCount: preview?.ChangeTargetCount, Items: preview?.Items, PreviewFingerprint: preview?.PreviewFingerprint);
            return new AgentExecuteOperationResponse(new AgentOperationResult(request.OperationId, request.OperationId,
                AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.PendingConfirmation), PendingOperation: pending);
        });
        return held;
    }

    private static Result<AiResponse> ReplayCall(string tool, string arguments) => Result.Success(new AiResponse
    {
        ToolCalls = [new(tool, Guid.NewGuid().ToString(), ParseArguments(arguments))],
        ConversationContext = new()
    });

    private void ReplyToReplay(string message) =>
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = message }));
}
