using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", null)]
    [InlineData("pt-BR", null)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    public async Task SavedMonthRetrospective_ReplaysNarrativeOrFactsOnly(string language, bool? generateNarrative)
    {
        var user = User.Create("Person", "person@example.com").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        EnableComposition();
        var from = Today.AddDays(-29);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId,
            "Reading", FrequencyUnit.Day, 1, DueDate: from)).Value;
        habit.Log(from);
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { habit });
        _payGate.CanUseRetrospective(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var narrative = new RetrospectiveNarrative(language == "en" ? "You read." : "Você leu.", "", "", "");
        var narratives = Substitute.For<IRetrospectiveService>();
        narratives.GenerateRetrospectiveAsync(Arg.Any<List<Habit>>(), from, Today, "month", language,
            Arg.Any<CancellationToken>(), 1).Returns(Result.Success(narrative));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var queryHandler = new GetRetrospectiveQueryHandler(_habitRepo, _payGate, narratives,
            _userStreakService, _userDateService, cache);
        var saved = await queryHandler.Handle(new(UserId, from, Today, "month", language), default);
        saved.IsSuccess.Should().BeTrue();
        saved.Value.FromCache.Should().BeFalse();
        saved.Value.Metrics.TotalCompletions.Should().Be(1);
        habit.Log(from.AddDays(1));
        var responses = new List<RetrospectiveResponse>();
        _mediator.Send(Arg.Any<GetRetrospectiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var response = await queryHandler.Handle(call.Arg<GetRetrospectiveQuery>(), call.Arg<CancellationToken>());
                responses.Add(response.Value);
                return response;
            });
        var tool = new GetRetrospectiveTool(_mediator, _userDateService);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        var arguments = new Dictionary<string, object>
        {
            ["period"] = "month",
            ["date_from"] = from.ToString("yyyy-MM-dd"),
            ["date_to"] = Today.ToString("yyyy-MM-dd"),
            ["orbit_read_complete"] = true
        };
        if (generateNarrative.HasValue)
            arguments["generate_narrative"] = generateNarrative.Value;
        SetupAiResponse(ToolResponse(tool.Name, "saved_month", JsonSerializer.Serialize(arguments)));
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["periodInsight"],
            SupportsPeriodInsightCard: true);
        var events = new List<ChatStreamEvent>();

        var result = await handler.Handle(new(UserId, language == "en"
            ? $"Show my saved month retrospective from {from:yyyy-MM-dd} through {Today:yyyy-MM-dd}, including its saved narrative, and indicate whether it needs refreshing."
            : $"Mostre minha retrospectiva mensal salva de {from:yyyy-MM-dd} até {Today:yyyy-MM-dd}, incluindo a narrativa salva, e indique se precisa atualizar.",
            ClientContext: client, StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        var includesNarrative = generateNarrative != false;
        result.IsSuccess.Should().BeTrue();
        result.Value.Operations.Should().ContainSingle().Which.Status.Should().Be(AgentOperationStatus.Succeeded);
        var part = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Subject;
        part.Kind.Should().Be("periodInsight");
        var card = part.Block.Should().BeOfType<PeriodInsightCard>().Subject;
        card.Period.Should().Be("month");
        card.DateFrom.Should().Be(from);
        card.DateTo.Should().Be(Today);
        card.TotalCompletions.Should().Be(includesNarrative ? 1 : 2);
        card.Narrative.Should().Be(includesNarrative ? saved.Value.Narrative : new RetrospectiveNarrative("", "", "", ""));
        card.Read.Should().NotBeNull();
        card.Read!.QueryFamily.Should().Be(tool.Name);
        card.Read.Completeness.Should().Be(includesNarrative ? "stale" : "complete");
        card.Read.Recovery.Should().Be(includesNarrative ? "refresh" : null);
        card.Read.RefreshToken.Should().NotBeNullOrWhiteSpace();
        responses.Should().ContainSingle().Which.FromCache.Should().Be(includesNarrative);
        await _mediator.Received(1).Send(Arg.Is<GetRetrospectiveQuery>(query =>
            query.GenerateNarrative == includesNarrative && query.Period == "month" && query.Language == language
            && query.DateFrom == from && query.DateTo == Today), Arg.Any<CancellationToken>());
        await narratives.Received(1).GenerateRetrospectiveAsync(Arg.Any<List<Habit>>(), from, Today,
            "month", language, Arg.Any<CancellationToken>(), 1);
        result.Value.AiMessage.Should().BeNullOrEmpty();
        events.Should().NotContain(item => item.Type == "delta");
        await _aiIntentService.DidNotReceive().ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());

        var stored = JsonSerializer.Deserialize<ChatResponse>(JsonSerializer.Serialize(result.Value,
            ChatStreamEvent.SerializerOptions), ChatStreamEvent.SerializerOptions)!;
        var replay = ChatResponseNegotiation.ForClient(stored, client, [], language, user.TimeZone);
        ChatStreamEvent.Final(replay).ToJson().Should().Be(ChatStreamEvent.Final(result.Value).ToJson());

        var modelRequest = _aiIntentService.ReceivedCalls().Single(call =>
            call.GetMethodInfo().Name == nameof(IAiIntentService.SendWithToolsAsync)).GetArguments()[0]
            .Should().BeOfType<AiToolRequest>().Subject;
        var declaration = modelRequest.ToolDeclarations.Select(item => JsonSerializer.SerializeToElement(item))
            .Single(item => item.GetProperty("name").GetString() == tool.Name);
        var guidance = declaration.GetProperty("parameters").GetProperty("properties")
            .GetProperty("generate_narrative").GetProperty("description").GetString();
        guidance.Should().Contain("Omit or pass true").And.Contain("saved narrative")
            .And.Contain("stale").And.Contain("False reads facts only").And.Contain("exact refresh");
    }
}
