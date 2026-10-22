using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class ChatControllerTests
{
    [Fact]
    public async Task RankingQuery_UsesAuthenticatedOwner_AndPublishesTheBlockPayload()
    {
        var from = new DateOnly(2024, 2, 29);
        var response = new HabitRankingResponse("query-id", "signed-query", "revision",
            new HabitRankingResolvedQuery(from, from.AddMonths(6), "America/Sao_Paulo", "completion_log_records",
                "Count of valid positive completion log records.", new HabitRankingScope(), [], 20),
            [new HabitRankingRow(1, Guid.NewGuid(), "Read", null, null, 2)], 1, 0, true, null);
        _mediator.Send(Arg.Any<GetHabitRankingQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success(response));

        var result = await _controller.QueryHabitRanking(new HabitRankingRequest(from, from.AddMonths(6)), default);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(response);
        await _mediator.Received(1).Send(Arg.Is<GetHabitRankingQuery>(q => q.UserId == UserId
            && q.DateFrom == from && q.DateToExclusive == from.AddMonths(6)), Arg.Any<CancellationToken>());
        ChatResponsePart part = new ChatBlockPart("habitRanking", HabitRankingCardBuilder.Build(response)!);
        var wire = JsonSerializer.SerializeToElement(part, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        wire.GetProperty("type").GetString().Should().Be("block");
        wire.GetProperty("kind").GetString().Should().Be("habitRanking");
        var payload = wire.GetProperty("block").GetProperty("ranking");
        payload.GetProperty("queryId").GetString().Should().Be(response.QueryId);
        payload.GetProperty("queryToken").GetString().Should().Be(response.QueryToken);
        payload.GetProperty("revision").GetString().Should().Be(response.Revision);
        payload.GetProperty("query").GetProperty("dateFrom").GetString().Should().Be("2024-02-29");
        payload.GetProperty("query").GetProperty("scope").GetProperty("includeChildren").GetBoolean().Should().BeTrue();
        payload.GetProperty("rows")[0].GetProperty("completedCount").GetInt32().Should().Be(2);
        payload.GetProperty("totalCount").GetInt32().Should().Be(1);
        payload.GetProperty("isComplete").GetBoolean().Should().BeTrue();
        payload.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RankingPage_UsesClaimAndBoundTokens_AndMapsStaleRevisionToConflict(bool refresh)
    {
        _mediator.Send(Arg.Any<GetHabitRankingQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<HabitRankingResponse>("Ranking changed.", "HABIT_RANKING_STALE"));
        var result = await _controller.GetHabitRankingPage("signed-query", refresh ? null : "signed-cursor", refresh);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(409);
        await _mediator.Received(1).Send(Arg.Is<GetHabitRankingQuery>(q => q.UserId == UserId
            && q.QueryToken == "signed-query" && q.Refresh == refresh
            && q.Cursor == (refresh ? null : "signed-cursor") && q.DateFrom == null), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public async Task RankingRoutes_RequireChatAndHabitScopesForApiKeys(bool chat, bool read, bool allowed)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, UserId.ToString()), new("auth_method", "api_key")];
        if (chat) claims.Add(new Claim("scope", AgentScopes.ChatInteract));
        if (read) claims.Add(new Claim("scope", AgentScopes.ReadHabits));
        _controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        _mediator.Send(Arg.Any<GetHabitRankingQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<HabitRankingResponse>("Invalid token.", "HABIT_RANKING_INVALID"));

        var query = await _controller.QueryHabitRanking(new HabitRankingRequest(), default);
        var page = await _controller.GetHabitRankingPage("signed-query", refresh: true);

        if (allowed)
        {
            query.Should().BeOfType<ObjectResult>();
            page.Should().BeOfType<ObjectResult>();
        }
        else
        {
            query.Should().BeOfType<ForbidResult>();
            page.Should().BeOfType<ForbidResult>();
            _mediator.ReceivedCalls().Should().BeEmpty();
        }
    }
}
