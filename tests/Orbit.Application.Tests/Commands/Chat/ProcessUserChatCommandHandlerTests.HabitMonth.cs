using Orbit.Domain.Interfaces;
using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("Show my habit calendar for this month", "en", false)]
    [InlineData("Show my month", "en", true)]
    [InlineData("How did my month go day by day?", "en", true)]
    [InlineData("Mostre meu calendário de hábitos deste mês", "pt-BR", false)]
    [InlineData("Mostre meu mês", "pt-BR", true)]
    [InlineData("Como foi meu mês dia a dia?", "pt-BR", true)]
    [InlineData("Show my habit calendar for this month", "en", true, true)]
    public async Task HabitMonth_ProductionReadProducesNegotiatedGridAndOneSentence(string message, string language,
        bool composition, bool longTitles = false)
    {
        var user = User.Create("Alex", "alex@test.com").Value;
        user.SetLanguage("pt-BR");
        SetupUserAndPayGate(user);
        if (composition) EnableComposition();
        var today = new DateOnly(2026, 10, 26);
        var start = new DateOnly(2026, 10, 1);
        var end = new DateOnly(2026, 10, 31);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(today);
        var habits = Enumerable.Range(0, 38).Select(index => Habit.Create(new(HabitSchedule.Recurring,
            UserId, longTitles ? $"Daily habit {index:D2}".PadRight(AppConstants.MaxHabitTitleLength, '\u6f22')
                : $"Daily habit {index:D2}", FrequencyUnit.Day, 1, start)).Value).ToList();
        var child = Habit.Create(new(HabitSchedule.Flexible, UserId, "Read", FrequencyUnit.Month, 20,
            start, ParentHabitId: habits[0].Id)).Value;
        var grandchild = Habit.Create(new(HabitSchedule.Recurring, UserId, "Reduce scrolling", FrequencyUnit.Day,
            1, start, IsBadHabit: true, ParentHabitId: child.Id)).Value;
        habits.AddRange([child, grandchild]);
        foreach (var habit in habits)
            for (var day = 0; day < 10; day++)
                habit.Log(start.AddDays(day), advanceDueDate: false).IsSuccess.Should().BeTrue();
        child.SkipFlexible(start.AddDays(10)).IsSuccess.Should().BeTrue();
        child.SkipFlexible(start.AddDays(11)).IsSuccess.Should().BeTrue();
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(habits.AsReadOnly());
        var calendarHandler = new GetCalendarMonthQueryHandler(_habitRepo, _userDateService, _unitOfWork, repeatReminderReader: Substitute.For<IRepeatReminderReader>());
        _mediator.Send(Arg.Any<GetCalendarMonthQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => calendarHandler.Handle(call.Arg<GetCalendarMonthQuery>(), call.Arg<CancellationToken>()));
        var monthHandler = new GetHabitMonthQueryHandler(_mediator, _userDateService);
        _mediator.Send(Arg.Any<GetHabitMonthQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => monthHandler.Handle(call.Arg<GetHabitMonthQuery>(), call.Arg<CancellationToken>()));
        var tool = new GetHabitMonthTool(_mediator);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(tool.Name, "month", JsonSerializer.Serialize(new { language })));
        ContinueWithReadDirective("There are 99 events. Some actions are unavailable.");
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["habitMonth"]);

        var result = await handler.Handle(new(UserId, message, ClientContext: client), default);

        result.IsSuccess.Should().BeTrue();
        var response = result.Value;
        var card = response.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Subject;
        card.Kind.Should().Be("habitMonth");
        var month = card.Block.Should().BeOfType<HabitMonthCard>().Subject;
        month.DateFrom.Should().Be(start);
        month.DateTo.Should().Be(end);
        var calendar = await calendarHandler.Handle(new(UserId, start, end), default);
        month.Calendar.Should().BeEquivalentTo(calendar.Value);
        var fallback = ProcessUserChatCommandHandler.ReadFallback(month, language);
        fallback.Should().Contain(grandchild.Title).And.NotMatchRegex(@"\d{4}-\d{2}-\d{2}")
            .And.Contain(language == "pt-BR" ? "31/10/2026" : "10/31/2026");
        month.Calendar.Logs.Should().HaveCount(40);
        month.Calendar.Logs.Values.Sum(logs => logs.Count).Should().Be(402);
        month.Calendar.Habits[0].ScheduledDates.Should().HaveCount(31);
        month.Calendar.Habits[0].Children.Single().Children.Single().Id.Should().Be(grandchild.Id);
        var sent = _aiIntentService.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(_aiIntentService.ContinueWithToolResultsAsync))
            .GetArguments()[1].Should().BeAssignableTo<IReadOnlyList<AiToolCallResult>>().Subject.Single();
        sent.ReadDirective.Should().Be("[[orbit:read:month]]");
        var payload = JsonSerializer.Serialize(sent.Payload, ChatStreamEvent.SerializerOptions);
        var full = JsonSerializer.Serialize(month.Calendar, ChatStreamEvent.SerializerOptions);
        Console.WriteLine($"Habit month model payload: {payload.Length}; complete calendar: {full.Length}");
        payload.Length.Should().BeLessThan(AppConstants.MaxAiToolPayloadJsonLength);
        using var model = JsonDocument.Parse(payload);
        model.RootElement.GetProperty("dateFrom").GetString().Should().Be("2026-10-01");
        model.RootElement.GetProperty("dateTo").GetString().Should().Be("2026-10-31");
        var summaries = model.RootElement.GetProperty("habits").EnumerateArray().ToArray();
        (summaries.Length + model.RootElement.GetProperty("omittedHabits").GetInt32()).Should().Be(40);
        if (longTitles)
        {
            model.RootElement.GetProperty("omittedHabits").GetInt32().Should().BePositive();
            month.Calendar.Habits.Should().OnlyContain(habit => habit.Title.Length == AppConstants.MaxHabitTitleLength);
        }
        else summaries.Should().HaveCount(40);
        var daily = summaries.Single(row => row.GetProperty("id").GetGuid() == habits[0].Id);
        daily.GetProperty("scheduled").GetInt32().Should().Be(31);
        daily.GetProperty("completed").GetInt32().Should().Be(10);
        daily.GetProperty("skipped").GetInt32().Should().Be(0);
        daily.GetProperty("missed").GetInt32().Should().Be(15);
        var flexible = summaries.Single(row => row.GetProperty("id").GetGuid() == child.Id);
        flexible.GetProperty("skipped").GetInt32().Should().Be(2);
        var bad = summaries.Single(row => row.GetProperty("id").GetGuid() == grandchild.Id);
        bad.GetProperty("completed").GetInt32().Should().Be(0);
        bad.GetProperty("missed").GetInt32().Should().Be(0);
        bad.GetProperty("slips").GetInt32().Should().Be(10);
        if (composition)
        {
            response.Parts!.OfType<ChatTextPart>().Should().BeEmpty();
            response.AiMessage.Should().BeNullOrEmpty();
        }
        else
        {
            response.Parts!.OfType<ChatTextPart>().Should().ContainSingle().Which.Text
                .Should().Be(HabitMonthCardBuilder.Introduction(month));
            response.AiMessage.Should().NotMatchRegex("[0-9]").And.NotContain("unavailable");
        }
        response.Calendar.Should().BeNull();
        month.Read!.QueryFamily.Should().Be("get_habit_month");
        await _mediator.DidNotReceive().Send(Arg.Any<GetCalendarEventsQuery>(), Arg.Any<CancellationToken>());
        var json = JsonSerializer.Serialize(response, ChatStreamEvent.SerializerOptions);
        var replay = JsonSerializer.Deserialize<ChatResponse>(json, ChatStreamEvent.SerializerOptions)!;
        ChatStreamEvent.Final(ChatResponseNegotiation.ForClient(replay, client, [])).ToJson()
            .Should().Be(ChatStreamEvent.Final(response).ToJson());
        var legacy = ChatResponseNegotiation.ForClient(replay, null, []);
        legacy.Parts.Should().BeNull();
        legacy.AiMessage.Should().Contain(grandchild.Title)
            .And.Contain(language == "pt-BR" ? "31/10/2026" : "10/31/2026")
            .And.NotContain(language == "en" ? "Habit calendar:" : "Calendário de hábitos:");
        var unknown = ChatResponseNegotiation.ForClient(replay, client with { PartKinds = ["calendar"] }, []);
        unknown.Parts!.OfType<ChatBlockPart>().Should().BeEmpty();
        unknown.AiMessage.Should().NotBeNullOrWhiteSpace();
    }
}
