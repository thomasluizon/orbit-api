using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", false, false)]
    [InlineData("en", true, false)]
    [InlineData("pt-BR", false, false)]
    [InlineData("pt-BR", true, false)]
    [InlineData("en", false, true)]
    [InlineData("en", true, true)]
    [InlineData("pt-BR", false, true)]
    [InlineData("pt-BR", true, true)]
    public async Task ThreeWeekRanking_SevenDaysOfRecords_ProducesRankingAndOneHistoryLine(
        string language, bool stream, bool emptyContinuation)
    {
        var user = User.Create("Reader", "ranking@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        EnableComposition();
        _userDateService.GetUserTimeZoneAsync(UserId, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        var read = Habit.Create(new(UserId, "Read", FrequencyUnit.Day, 1, DueDate: Today.AddDays(-6))).Value;
        var walk = Habit.Create(new(UserId, "Walk", FrequencyUnit.Day, 1, DueDate: Today.AddDays(-6))).Value;
        for (var day = -6; day <= 0; day++)
        {
            read.Log(Today.AddDays(day), advanceDueDate: false);
            if (day % 2 == 0) walk.Log(Today.AddDays(day), advanceDueDate: false);
        }
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>()).Returns([read, walk]);
        var tokens = new HabitRankingTokenService(Options.Create(new JwtSettings
        {
            SecretKey = "three-week-ranking-test-signing-secret",
            Issuer = "test",
            Audience = "test"
        }));
        var queryHandler = new GetHabitRankingQueryHandler(_habitRepo, _userDateService, tokens,
            _productAnalytics, NullLogger<GetHabitRankingQueryHandler>.Instance);
        _mediator.Send(Arg.Any<GetHabitRankingQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => queryHandler.Handle(call.Arg<GetHabitRankingQuery>(), call.Arg<CancellationToken>()));
        var tool = new GetHabitRankingTool(_mediator);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        var args = JsonSerializer.Serialize(new
        {
            date_from = Today.AddDays(-20).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            date_to_exclusive = Today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        });
        SetupAiResponse(ToolResponse(tool.Name, "ranking", args));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(emptyContinuation ? Result.Failure<AiResponse>(Orbit.Application.Common.ErrorMessages.AiEmptyResponse)
                : Result.Success(new AiResponse { TextMessage = "[[orbit:ranking]]" }));
        var events = new List<ChatStreamEvent>();
        var question = language == "en" ? "Which habits did I complete most in the last 3 weeks?"
            : "Quais hábitos eu mais concluí nas últimas 3 semanas?";

        var result = await handler.Handle(new(UserId, question,
            ClientContext: new(SupportsParts: true, PartKinds: ["habitRanking"]),
            StreamSink: stream ? item => { events.Add(item); return Task.CompletedTask; }
        : null), default);

        result.IsSuccess.Should().BeTrue();
        var parts = result.Value.Parts!;
        parts.Should().HaveCount(2);
        var ranking = parts[0].Should().BeOfType<ChatBlockPart>().Which.Block
            .Should().BeOfType<HabitRankingCard>().Which.Ranking;
        ranking.Query.DateFrom.Should().Be(Today.AddDays(-20));
        ranking.Query.DateToExclusive.Should().Be(Today.AddDays(1));
        ranking.Rows.Select(row => (row.Name, row.CompletedCount)).Should().Equal(("Read", 7), ("Walk", 4));
        var notice = parts[1].Should().BeOfType<ChatTextPart>().Which.Text;
        notice.Should().Be(language == "en" ? "Recorded completions in this window start on 2026-03-28."
            : "As conclusões registradas neste período começam em 2026-03-28.");
        result.Value.AiMessage.Should().Be(notice);
        result.Value.Operations.Should().ContainSingle(operation => operation.OperationId == "get_habit_ranking"
            && operation.Status == AgentOperationStatus.Succeeded);
        var json = JsonSerializer.Serialize(result.Value);
        json.Should().NotContain("FirstRecordedDate");
        events.Where(item => item.Type == "delta").Should().BeEmpty();
        await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Is<AiToolRequest>(request =>
            request.DynamicContext!.Contains("date_from=today-20 days")
            && request.DynamicContext.Contains("Do not ask to choose rolling or calendar weeks")),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }
}
