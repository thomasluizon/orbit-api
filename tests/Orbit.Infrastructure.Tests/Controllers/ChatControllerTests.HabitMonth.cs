using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class ChatControllerTests
{
    [Fact]
    public async Task HabitMonthRefresh_UsesDedicatedContractAndAuthenticatedOwner()
    {
        var context = new AgentClientContext(SupportsParts: true, PartKinds: ["habitMonth"]);
        var from = new DateOnly(2026, 4, 1);
        var to = new DateOnly(2026, 4, 30);
        var read = new ReadBlockMetadata(Guid.NewGuid(), "get_habit_month",
            JsonSerializer.SerializeToElement(new { date_from = from, date_to = to, language = "pt-BR" }),
            "revision", "complete", "refreshed-token");
        var card = new HabitMonthCard(from, to, from, 1, new CalendarMonthResponse([], []), "pt-BR", Read: read);
        _mediator.Send(Arg.Any<RefreshReadBlockQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new QueryReadBlock("habitMonth", card, read)));

        var result = await _controller.RefreshHabitMonthReadBlock(new("original-token", context), default);

        var response = result.Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<HabitMonthReadBlockRefreshResponse>().Subject;
        response.Kind.Should().Be("habitMonth");
        response.Block.Should().Be(card);
        response.Read.Should().Be(read);
        var wire = JsonSerializer.SerializeToElement(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        wire.GetProperty("block").GetProperty("calendar").GetProperty("habits").GetArrayLength().Should().Be(0);
        wire.GetProperty("block").GetProperty("dateFrom").GetString().Should().Be("2026-04-01");
        wire.GetProperty("block").TryGetProperty("$type", out _).Should().BeFalse();
        await _mediator.Received(1).Send(Arg.Is<RefreshReadBlockQuery>(query => query.UserId == UserId
            && query.Token == "original-token" && query.ClientContext == context && query.HabitMonthOnly
            && !query.GamificationOnly && !query.Page && query.AuthMethod == AgentAuthMethod.Jwt), Arg.Any<CancellationToken>());
        typeof(ChatController).GetMethod(nameof(ChatController.RefreshHabitMonthReadBlock))!
            .GetCustomAttributes(typeof(HttpPostAttribute), true).Cast<HttpPostAttribute>()
            .Single().Template.Should().Be("habit-month/refresh");
    }

    [Theory]
    [InlineData("api_key", false, false)]
    [InlineData("api_key", true, false)]
    [InlineData("api_key", false, true)]
    [InlineData("api_key", true, true)]
    [InlineData("oauth", false, false)]
    [InlineData("oauth", true, false)]
    [InlineData("oauth", false, true)]
    [InlineData("oauth", true, true)]
    public async Task HabitMonthRefresh_ScopedCredentialsRequireBothScopesAndForwardRestrictions(string authMethod, bool chat, bool read)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, UserId.ToString()), new("auth_method", authMethod),
            new("api_key_read_only", "true")];
        if (chat) claims.Add(new("scope", AgentScopes.ChatInteract.ToUpperInvariant()));
        if (read) claims.Add(new("scope", AgentScopes.ReadHabits.ToUpperInvariant()));
        _controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        _mediator.Send(Arg.Any<RefreshReadBlockQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<QueryReadBlock>("Query unavailable.", "READ_QUERY_EXPIRED"));

        var result = await _controller.RefreshHabitMonthReadBlock(new("token"), default);

        if (chat && read)
        {
            result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(410);
            var expectedAuth = authMethod == "oauth" ? AgentAuthMethod.OAuth : AgentAuthMethod.ApiKey;
            await _mediator.Received(1).Send(Arg.Is<RefreshReadBlockQuery>(query => query.AuthMethod == expectedAuth
                && query.ReadOnlyCredential && query.GrantedScopes != null
                && query.GrantedScopes.Contains(AgentScopes.ChatInteract, StringComparer.OrdinalIgnoreCase)
                && query.GrantedScopes.Contains(AgentScopes.ReadHabits, StringComparer.OrdinalIgnoreCase)
                && query.HabitMonthOnly && !query.GamificationOnly), Arg.Any<CancellationToken>());
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
    public void HabitMonthRefresh_OpenApiPublishesASeparateTypedResponse(string file)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", "src/Orbit.Api", file));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        schemas.GetProperty("HabitMonthReadBlockRefreshResponse").GetProperty("properties")
            .GetProperty("block").GetProperty("$ref").GetString().Should().Be("#/components/schemas/HabitMonthCard");
        schemas.GetProperty("RefreshHabitMonthReadBlockRequest").GetProperty("properties")
            .TryGetProperty("page", out _).Should().BeFalse();
        foreach (var media in new[] { "application/json", "text/json", "text/plain" })
        {
            document.RootElement.GetProperty("paths").GetProperty("/api/Chat/habit-month/refresh")
                .GetProperty("post").GetProperty("responses").GetProperty("200").GetProperty("content")
                .GetProperty(media).GetProperty("schema").GetProperty("$ref").GetString()
                .Should().Be("#/components/schemas/HabitMonthReadBlockRefreshResponse");
        }
    }
}
