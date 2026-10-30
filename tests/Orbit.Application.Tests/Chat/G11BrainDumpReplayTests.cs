using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(1, false)]
    [InlineData(3, false)]
    public async Task G11_CapturedSingleIdeaKeepsEveryItemInOneHeldPreview(int repeat, bool composition)
    {
        using var capture = G11BrainDumpCapture(repeat);
        SetupG11BrainDump(capture, composition);
        var draft = G11SuggestionDrafts(capture).Single();
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
        var calls = BrainDumpCalls(capture);
        SetupGateTenRounds(calls);

        var result = await handler.Handle(new(UserId, "Prepare os itens solicitados para aprovação.",
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true,
                SupportsHabitListCard: true, PartKinds: ["habitList", "habitSuggestions", "pickList"], Locale: "pt-BR")), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        held.Should().ContainSingle().Which.OperationId.Should().Be("bulk_create_habits");
        var items = result.Value.PendingOperations.Should().ContainSingle().Which.Items!;
        var proposed = calls[2].Args.GetProperty("habits").EnumerateArray().ToArray();
        items.Select(item => item.EntityName).Should().Equal(proposed.Select(item => item.GetProperty("title").GetString()));
        items.Select(item => item.HabitType).Should().Equal("Task", "Habit", "Habit");
        items[0].Fields.Should().Contain(field => field.Field == "due_date" && field.NewValue == "2026-10-31");
        items[1].Fields.Should().Contain(field => field.Field == "due_date" && field.NewValue == "2026-10-30");
        items[1].Fields.Should().Contain(field => field.Field == "due_time" && field.NewValue == "08:00");
        items[1].Fields.Should().Contain(field => field.Field == "frequency_unit" && field.NewValue == "Day");
        items[2].EntityName.Should().Be(draft.Title);
        items[2].Fields.Should().Contain(field => field.Field == "due_time" && field.NewValue == draft.DueTime.ToString("HH:mm"));
        items[2].Fields.Should().Contain(field => field.Field == "due_end_time" && field.NewValue == draft.DueEndTime.ToString("HH:mm"));
        result.Value.Parts.Should().BeEmpty();
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.Actions.Should().BeEmpty();
        await generator.Received(1).GenerateAsync(Arg.Is<HabitSuggestionRequest>(request => request.Count == 1), Arg.Any<CancellationToken>());
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task G11_CapturedAskIsVisibleBesideHistoricalSuggestionResults(bool composition)
    {
        using var capture = G11BrainDumpCapture(2);
        SetupG11BrainDump(capture, composition);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var suggest = new SuggestHabitsTool(_mediator);
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(query, suggest, bulk, RealAskTool());
        UseProductionReadExecutor(query);
        var card = new HabitSuggestionsCard("pt-BR", G11SuggestionDrafts(capture));
        _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId == suggest.Name),
                Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(new(suggest.Name, suggest.Name, AgentRiskClass.Low,
                AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded, Payload: card)));
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var calls = BrainDumpCalls(capture);
        SetupGateTenRounds(calls);

        var result = await handler.Handle(new(UserId, "Prepare os itens solicitados para aprovação.",
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true,
                SupportsHabitListCard: true, PartKinds: ["habitList", "habitSuggestions", "pickList"], Locale: "pt-BR")), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        await _blockStore.Received(1).AddAsync(Arg.Any<IssuedAstraBlock>(), Arg.Any<CancellationToken>());
        var part = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Subject;
        part.Kind.Should().Be("pickList");
        var block = part.Block.Should().BeOfType<AstraPickListBlock>().Subject;
        var ask = calls[2].Args;
        block.Title.Should().Be(ask.GetProperty("title").GetString());
        block.Action.Should().Be(ask.GetProperty("action").GetString());
        block.Items.Select(item => item.Id).Should().Equal(ask.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()));
        block.Items.Select(item => item.Label).Should().Equal(card.Drafts.Select(draft => draft.Title));
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        result.Value.Actions.Should().BeEmpty();
        result.Value.AiMessage.Should().BeNullOrEmpty();
        await _operationExecutor.Received(1).ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request =>
            request.OperationId == suggest.Name && request.Arguments.GetProperty("count").ValueKind == JsonValueKind.Null), Arg.Any<CancellationToken>());
        await _operationExecutor.DidNotReceive().ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId == bulk.Name), Arg.Any<CancellationToken>());
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    private static JsonDocument G11BrainDumpCapture(int repeat) => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "BrainDumps", $"g11-brain-dump-ideas-pt-BR-r{repeat}.json")));

    private void SetupG11BrainDump(JsonDocument capture, bool composition)
    {
        SetupUserAndPayGate();
        if (composition) EnableComposition();
        var today = new DateOnly(2026, 10, 30);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(today);
        var rows = capture.RootElement.GetProperty("successfulTools")[0].GetProperty("result").GetProperty("items");
        SetupGateTenHabits(rows.EnumerateArray().Select(row => Habit.Create(new HabitCreateParams(
            HabitSchedule.Recurring, UserId, row.GetProperty("title").GetString()!, FrequencyUnit.Day, 1, today)).Value).ToArray());
    }

    private static HabitSuggestionDraft[] G11SuggestionDrafts(JsonDocument capture)
    {
        var drafts = capture.RootElement.GetProperty("successfulTools").EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "suggest_habits")
            .GetProperty("result").GetProperty("drafts");
        var bulk = capture.RootElement.GetProperty("toolCalls").EnumerateArray()
            .FirstOrDefault(call => call.GetProperty("name").GetString() == "bulk_create_habits");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        return drafts.EnumerateArray().Select(recorded =>
        {
            var draft = JsonNode.Parse(recorded.GetRawText())!.AsObject();
            foreach (var (field, argument) in new[] { ("title", "title"), ("dueDate", "due_date"), ("dueTime", "due_time"), ("dueEndTime", "due_end_time") })
                if (draft[field]!.GetValue<string>().Contains("[redacted]", StringComparison.Ordinal))
                {
                    var value = bulk.GetProperty("arguments").GetProperty("habits")[2].GetProperty(argument).GetString()!;
                    draft[field] = field is "dueTime" or "dueEndTime" ? TimeOnly.Parse(value).ToString("HH:mm:ss") : value;
                }
            return draft.Deserialize<HabitSuggestionDraft>(options)! with { Id = Guid.NewGuid() };
        }).ToArray();
    }
}
