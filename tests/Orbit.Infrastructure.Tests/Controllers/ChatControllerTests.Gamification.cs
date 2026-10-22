using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Gamification.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class ChatControllerTests
{
    [Fact]
    public async Task GamificationRefresh_UsesDedicatedContractAndAuthenticatedOwner()
    {
        var context = new AgentClientContext(SupportsParts: true, PartKinds: ["gamification"]);
        var read = new ReadBlockMetadata(Guid.NewGuid(), "get_gamification_overview",
            JsonSerializer.SerializeToElement(new { include_profile = false, include_achievements = true, include_streak = false }),
            "revision", "complete", "refreshed-token");
        var card = new GamificationCard(new GamificationOverviewPayload(null, new AchievementsResponse([]), null), Read: read);
        _mediator.Send(Arg.Any<RefreshReadBlockQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new QueryReadBlock("gamification", card, read)));

        var result = await _controller.RefreshGamificationReadBlock(new("original-token", context), default);

        var response = result.Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<GamificationReadBlockRefreshResponse>().Subject;
        response.Kind.Should().Be("gamification");
        response.Block.Should().Be(card);
        response.Read.Should().Be(read);
        var wire = JsonSerializer.SerializeToElement(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        wire.GetProperty("block").GetProperty("state").GetProperty("profile").ValueKind.Should().Be(JsonValueKind.Null);
        wire.GetProperty("block").GetProperty("state").GetProperty("streak").ValueKind.Should().Be(JsonValueKind.Null);
        wire.GetProperty("block").TryGetProperty("$type", out _).Should().BeFalse();
        await _mediator.Received(1).Send(Arg.Is<RefreshReadBlockQuery>(query => query.UserId == UserId
            && query.Token == "original-token" && query.ClientContext == context && query.GamificationOnly
            && !query.Page && query.AuthMethod == AgentAuthMethod.Jwt), Arg.Any<CancellationToken>());
        typeof(ChatController).GetMethod(nameof(ChatController.RefreshGamificationReadBlock))!
            .GetCustomAttributes(typeof(HttpPostAttribute), true).Cast<HttpPostAttribute>()
            .Single().Template.Should().Be("gamification/refresh");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task GamificationRefresh_ApiKeysRequireBothScopesAndForwardCredentialRestrictions(bool chat, bool read)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, UserId.ToString()), new("auth_method", "api_key"),
            new("api_key_read_only", "true")];
        if (chat) claims.Add(new("scope", AgentScopes.ChatInteract));
        if (read) claims.Add(new("scope", AgentScopes.ReadGamification));
        _controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        _mediator.Send(Arg.Any<RefreshReadBlockQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<QueryReadBlock>("Query unavailable.", "READ_QUERY_EXPIRED"));

        var result = await _controller.RefreshGamificationReadBlock(new("token"), default);

        if (chat && read)
        {
            result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(410);
            await _mediator.Received(1).Send(Arg.Is<RefreshReadBlockQuery>(query => query.AuthMethod == AgentAuthMethod.ApiKey
                && query.ReadOnlyCredential && query.GrantedScopes != null && query.GrantedScopes.Contains(AgentScopes.ChatInteract)
                && query.GrantedScopes.Contains(AgentScopes.ReadGamification) && query.GamificationOnly), Arg.Any<CancellationToken>());
        }
        else
        {
            result.Should().BeOfType<ForbidResult>();
            _mediator.ReceivedCalls().Should().BeEmpty();
        }
    }

    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v1-r2.json")]
    [InlineData("openapi_v2.json")]
    [InlineData("openapi_v2-r2.json")]
    public void GamificationRefresh_OpenApiKeepsTheGenericUnionAndPublishesASeparateResponse(string file)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", "src/Orbit.Api", file));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        schemas.GetProperty("ReadBlockCard").GetProperty("anyOf").EnumerateArray()
            .Select(item => item.GetProperty("$ref").GetString()).Should().BeEquivalentTo(
                new[] { "HabitListCard", "GoalListCard", "MetricsCard", "PeriodInsightCard", "DaySummaryCard", "StreakCard",
                    "CalendarCard", "RecordListCard", "AccountRowsCard", "PeriodTotalCard", "PeriodComparisonCard" }
                    .Select(name => $"#/components/schemas/ReadBlockCard{name}"));
        schemas.GetProperty("GamificationReadBlockRefreshResponse").GetProperty("properties")
            .GetProperty("block").GetProperty("$ref").GetString().Should().Be("#/components/schemas/GamificationCard");
        schemas.GetProperty("RefreshGamificationReadBlockRequest").GetProperty("properties")
            .TryGetProperty("page", out _).Should().BeFalse();
        var paths = document.RootElement.GetProperty("paths");
        foreach (var media in new[] { "application/json", "text/json", "text/plain" })
        {
            paths.GetProperty("/api/Chat/reads/refresh").GetProperty("post").GetProperty("responses")
                .GetProperty("200").GetProperty("content").GetProperty(media).GetProperty("schema")
                .GetProperty("$ref").GetString().Should().Be("#/components/schemas/ReadBlockRefreshResponse");
            paths.GetProperty("/api/Chat/gamification/refresh").GetProperty("post").GetProperty("responses")
                .GetProperty("200").GetProperty("content").GetProperty(media).GetProperty("schema")
                .GetProperty("$ref").GetString().Should().Be("#/components/schemas/GamificationReadBlockRefreshResponse");
        }
    }
}
