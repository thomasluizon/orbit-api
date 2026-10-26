using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
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
    public async Task HabitMonth_ProductionReadProducesNegotiatedGridAndOneSentence(string message, string language, bool composition)
    {
        SetupUserAndPayGate();
        if (composition) EnableComposition();
        var calendarHandler = new GetCalendarMonthQueryHandler(_habitRepo, _userDateService, _unitOfWork);
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
        month.DateFrom.Should().Be(new DateOnly(Today.Year, Today.Month, 1));
        month.DateTo.Should().Be(new DateOnly(Today.Year, Today.Month, DateTime.DaysInMonth(Today.Year, Today.Month)));
        response.Parts!.OfType<ChatTextPart>().Should().ContainSingle().Which.Text
            .Should().Be(HabitMonthCardBuilder.Introduction(month));
        response.AiMessage.Should().NotMatchRegex("[0-9]").And.NotContain("unavailable");
        response.Calendar.Should().BeNull();
        month.Read!.QueryFamily.Should().Be("get_habit_month");
        await _mediator.DidNotReceive().Send(Arg.Any<GetCalendarEventsQuery>(), Arg.Any<CancellationToken>());
        var json = JsonSerializer.Serialize(response, ChatStreamEvent.SerializerOptions);
        var replay = JsonSerializer.Deserialize<ChatResponse>(json, ChatStreamEvent.SerializerOptions)!;
        ChatStreamEvent.Final(ChatResponseNegotiation.ForClient(replay, client, [])).ToJson()
            .Should().Be(ChatStreamEvent.Final(response).ToJson());
        var legacy = ChatResponseNegotiation.ForClient(replay, null, []);
        legacy.Parts.Should().BeNull();
        legacy.AiMessage.Should().Contain(language == "en" ? "Habit calendar" : "Calendário de hábitos");
        var unknown = ChatResponseNegotiation.ForClient(replay, client with { PartKinds = ["calendar"] }, []);
        unknown.Parts!.OfType<ChatBlockPart>().Should().BeEmpty();
        unknown.AiMessage.Should().NotBeNullOrWhiteSpace();
    }
}
