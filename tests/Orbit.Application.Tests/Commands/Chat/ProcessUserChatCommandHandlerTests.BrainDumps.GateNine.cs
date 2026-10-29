using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("mixed-en-r3", "en")]
    [InlineData("mixed-pt-BR-r2", "pt-BR")]
    [InlineData("reuse-en-r1", "en")]
    [InlineData("reuse-en-r2", "en")]
    [InlineData("reuse-en-r3", "en")]
    [InlineData("reuse-pt-BR-r2", "pt-BR")]
    [InlineData("reuse-en-r1", "en", "WATER")]
    [InlineData("reuse-pt-BR-r2", "pt-BR", "Beber Água")]
    public async Task BrainDump_GateNineCaptureKeepsIdentityAndEffectiveStartDate(string name, string language, string? storedTitle = null)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var today = new DateOnly(2026, 10, 29);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(today);
        var existing = Habit.Create(new(HabitSchedule.Recurring, UserId,
            storedTitle ?? (language == "en" ? "Water" : "Beber água"), FrequencyUnit.Day, 1, today)).Value;
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { existing }.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToArray());
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { existing }.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToArray());
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "BrainDumps", "g9a-brain-dump-" + name + ".json")).Replace("[redacted]", existing.Id.ToString()));
        var calls = BrainDumpCalls(fixture);
        var bulk = new BulkCreateHabitsTool(_mediator);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(query, bulk);
        var held = HoldBrainDumpWithReads(bulk, query);
        var reads = calls.Where(call => call.Name != bulk.Name).ToArray();
        var write = new AiResponse { ToolCalls = calls.Where(call => call.Name == bulk.Name).ToArray(), ConversationContext = new() };
        SetupAiResponse(reads.Length == 0 ? write : new AiResponse { ToolCalls = reads, ConversationContext = new() });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Orbit.Domain.Common.Result.Success(write));

        var result = await handler.Handle(new(UserId, fixture.RootElement.GetProperty("message").GetString()!,
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true,
                SupportsHabitListCard: true, PartKinds: ["habitList"], Locale: language)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        held.Should().ContainSingle();
        var preview = result.Value.PendingOperations.Should().ContainSingle().Subject;
        var stretch = preview.Items![1];
        stretch.HabitType.Should().Be("Habit");
        stretch.Fields.Should().ContainSingle(field => field.Field == "due_date" && field.NewValue == "2026-10-29");
        stretch.Fields.Should().ContainSingle(field => field.Field == "due_time" && field.NewValue == "08:00");
        if (stretch.Fields.Any(field => field.Field == "start_date"))
        {
            stretch.Fields.Single(field => field.Field == "start_date").IsEditable.Should().BeTrue();
            stretch.Fields.Single(field => field.Field == "due_date").IsEditable.Should().BeFalse();
        }
        if (preview.Items.Count == 3)
        {
            var reused = preview.Items[2];
            reused.EntityId.Should().Be(existing.Id);
            reused.EntityName.Should().Be(existing.Title);
            reused.Fields.Single(field => field.Field == "habit_id").ProposedValue!.Value.GetGuid().Should().Be(existing.Id);
            reused.Fields.Single(field => field.Field == "title").NewValue.Should().Be(existing.Title);
        }
        result.Value.Parts.Should().BeEmpty();
        result.Value.AiMessage.Should().BeNullOrEmpty();
        var replay = JsonSerializer.Deserialize<ChatResponse>(JsonSerializer.Serialize(result.Value,
            ChatStreamEvent.SerializerOptions), ChatStreamEvent.SerializerOptions)!;
        ChatStreamEvent.Final(replay).ToJson().Should().Be(ChatStreamEvent.Final(result.Value).ToJson());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task BrainDump_GateNineIdeaCaptureKeepsSuggestionAndExplicitActivities(int repeat)
    {
        SetupUserAndPayGate();
        EnableComposition();
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 10, 29));
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "BrainDumps", $"g9a-brain-dump-ideas-en-r{repeat}.json")));
        var recorded = fixture.RootElement.GetProperty("groundingEvidence").GetProperty("successfulTools").EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "suggest_habits").GetProperty("result").GetProperty("drafts")[0];
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var draft = recorded.Deserialize<HabitSuggestionDraft>(options)! with { Id = Guid.NewGuid() };
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
        var calls = BrainDumpCalls(fixture);
        SetupAiResponse(new AiResponse { ToolCalls = calls.Where(call => call.Name != bulk.Name).ToArray(), ConversationContext = new() });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ToolCalls = calls.Where(call => call.Name == bulk.Name).ToArray(), ConversationContext = new() }));

        var result = await handler.Handle(new(UserId, fixture.RootElement.GetProperty("message").GetString()!,
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true,
                SupportsHabitListCard: true, PartKinds: ["habitList", "habitSuggestions"], Locale: "en")), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        held.Should().ContainSingle();
        var preview = result.Value.PendingOperations.Should().ContainSingle().Subject;
        preview.Items!.Select(item => item.HabitType).Should().Equal("Task", "Habit", "Habit");
        var idea = preview.Items![2];
        idea.EntityName.Should().Be(draft.Title);
        idea.Fields.Should().ContainSingle(field => field.Field == "due_time" && field.NewValue == draft.DueTime.ToString("HH:mm"));
        idea.Fields.Should().ContainSingle(field => field.Field == "due_end_time" && field.NewValue == draft.DueEndTime.ToString("HH:mm"));
        result.Value.Parts.Should().BeEmpty();
        result.Value.AiMessage.Should().BeNullOrEmpty();
    }
}
