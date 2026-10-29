using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Profile.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", 2, false)]
    [InlineData("en", 2, true)]
    [InlineData("pt-BR", 1, false)]
    [InlineData("pt-BR", 1, true)]
    [InlineData("pt-BR", 3, false)]
    [InlineData("pt-BR", 3, true)]
    public async Task BrainDump_CapturedIdeaReadsDoNotAddCalendarProfileOrSuggestionBlocks(
        string language, int repeat, bool composition)
    {
        SetupUserAndPayGate();
        if (composition) EnableComposition();
        var today = new DateOnly(2026, 10, 28);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(today);
        using var fixture = BrainDumpCapture("ideas-" + language + "-r" + repeat, Guid.NewGuid());
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var successful = fixture.RootElement.GetProperty("successfulTools").EnumerateArray().ToArray();
        var recorded = successful.Single(tool => tool.GetProperty("name").GetString() == "suggest_habits")
            .GetProperty("result").GetProperty("drafts")[0];
        var draft = recorded.Deserialize<HabitSuggestionDraft>(options)! with { Id = Guid.NewGuid() };
        var generator = Substitute.For<Orbit.Domain.Interfaces.IHabitDraftGenerator>();
        generator.GenerateAsync(Arg.Any<HabitSuggestionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<HabitSuggestionDraft>>([draft]));
        var suggestions = new SuggestHabitDraftsQueryHandler(new HabitSuggestionService(_habitRepo, _goalRepo, _userDateService, generator));
        _mediator.Send(Arg.Any<SuggestHabitDraftsQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => suggestions.Handle(call.Arg<SuggestHabitDraftsQuery>(), call.Arg<CancellationToken>()));
        var profile = successful.FirstOrDefault(tool => tool.GetProperty("name").GetString() == "get_profile");
        if (profile.ValueKind != JsonValueKind.Undefined)
            _mediator.Send(Arg.Any<GetProfileQuery>(), Arg.Any<CancellationToken>())
                .Returns(Result.Success(profile.GetProperty("result").Deserialize<ProfileResponse>(options)!));
        var calendar = new GetCalendarMonthQueryHandler(_habitRepo, _userDateService, _unitOfWork,
            repeatReminderReader: Substitute.For<Orbit.Domain.Interfaces.IRepeatReminderReader>());
        _mediator.Send(Arg.Any<GetCalendarMonthQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => calendar.Handle(call.Arg<GetCalendarMonthQuery>(), call.Arg<CancellationToken>()));
        var month = new GetHabitMonthQueryHandler(_mediator, _userDateService);
        _mediator.Send(Arg.Any<GetHabitMonthQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => month.Handle(call.Arg<GetHabitMonthQuery>(), call.Arg<CancellationToken>()));
        var bulk = new BulkCreateHabitsTool(_mediator);
        Orbit.Application.Chat.Tools.IAiTool[] reads = [new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader),
            new SuggestHabitsTool(_mediator), new GetProfileTool(_mediator), new GetHabitMonthTool(_mediator)];
        var handler = CreateHandler([bulk, .. reads]);
        var held = HoldBrainDumpWithReads(bulk, reads);
        var calls = BrainDumpCalls(fixture);
        SetupAiResponse(new AiResponse { ToolCalls = calls.Where(call => call.Name != bulk.Name).ToArray(), ConversationContext = new() });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ToolCalls = calls.Where(call => call.Name == bulk.Name).ToArray(), ConversationContext = new() }));
        var events = new List<ChatStreamEvent>();

        var result = await handler.Handle(new(UserId, fixture.RootElement.GetProperty("message").GetString()!,
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true,
                SupportsHabitListCard: true, SupportsAccountRowsCard: true,
                PartKinds: ["habitList", "habitMonth", "accountRows", "habitSuggestions"], Locale: language),
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        held.Should().ContainSingle();
        result.Value.Operations.Should().HaveCount(calls.Length - 1).And.OnlyContain(operation => operation.Status == AgentOperationStatus.Succeeded);
        var preview = result.Value.PendingOperations.Should().ContainSingle().Subject;
        preview.Items!.Select(item => item.HabitType).Should().Equal("Task", "Habit", "Habit");
        var idea = preview.Items!.Single(item => item.EntityName == draft.Title);
        foreach (var (field, expected) in new Dictionary<string, string>
        {
            ["title"] = draft.Title,
            ["emoji"] = draft.Emoji,
            ["frequency_unit"] = draft.FrequencyUnit.ToString()!,
            ["frequency_quantity"] = draft.FrequencyQuantity.ToString()!,
            ["due_date"] = draft.DueDate.ToString("yyyy-MM-dd"),
            ["due_time"] = draft.DueTime.ToString("HH:mm"),
            ["due_end_time"] = draft.DueEndTime.ToString("HH:mm")
        })
            idea.Fields.Single(change => change.Field == field).NewValue.Should().Be(expected);
        result.Value.Parts.Should().BeEmpty();
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.Actions.Should().BeEmpty();
        result.Value.FollowUps.Should().BeNullOrEmpty();
        events.Should().NotContain(item => item.Type == "delta");
        var replay = JsonSerializer.Deserialize<ChatResponse>(JsonSerializer.Serialize(result.Value,
            ChatStreamEvent.SerializerOptions), ChatStreamEvent.SerializerOptions)!;
        ChatStreamEvent.Final(replay).ToJson().Should().Be(ChatStreamEvent.Final(result.Value).ToJson());
    }
}
