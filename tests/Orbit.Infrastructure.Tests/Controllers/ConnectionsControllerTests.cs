using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Connections;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Services.Notion;
using Orbit.Infrastructure.Tests.Services.Notion;

namespace Orbit.Infrastructure.Tests.Controllers;

public class ConnectionsControllerTests
{
    [Fact]
    public async Task ValidStateConnectsOnceAndReplayIsRejected()
    {
        using var test = new NotionTestContext();
        var controller = test.Controller();
        var state = await AuthorizeAsync(controller);
        (await CompleteAsync(controller, state)).Should().BeOfType<NoContentResult>();
        (await CompleteAsync(controller, state)).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        (await test.Db.NotionConnections.CountAsync()).Should().Be(1);
        (await test.Db.NotionOAuthStates.CountAsync()).Should().Be(0);
        await test.Api.Received(1).ExchangeAsync("code", NotionTestContext.RedirectUri, Arg.Any<CancellationToken>());
        test.Analytics.Received(1).CaptureUserEvent(test.User.Id, "notion_connected", "pro");
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("foreign")]
    [InlineData("unknown")]
    [InlineData("redirect")]
    public async Task InvalidStateStoresNothingAndNeverCallsNotion(string kind)
    {
        using var test = new NotionTestContext();
        var owner = test.Controller();
        var state = await AuthorizeAsync(owner);
        if (kind == "expired") test.Clock.Now = test.Clock.Now.AddMinutes(10);
        if (kind == "unknown") state = new string('f', 64);
        var controller = kind == "foreign" ? test.Controller(Guid.NewGuid()) : owner;
        if (kind == "foreign")
        {
            var other = User.Create("Other", "other@example.com").Value;
            test.Db.Users.Add(other);
            await test.Db.SaveChangesAsync();
            controller = test.Controller(other.Id);
        }
        var response = await controller.CompleteNotion(new("code", state,
            kind == "redirect" ? "https://attacker.example/callback" : NotionTestContext.RedirectUri), CancellationToken.None);
        response.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        (await test.Db.NotionConnections.CountAsync()).Should().Be(0);
        await test.Api.DidNotReceive().ExchangeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        if (kind == "foreign") (await CompleteAsync(owner, state)).Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task ConcurrentCallbackCannotExchangeTheSameStateTwice()
    {
        using var test = new NotionTestContext();
        var controller = test.Controller();
        var state = await AuthorizeAsync(controller);
        var pending = new TaskCompletionSource<NotionCredentials>();
        test.Api.ExchangeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(pending.Task);
        var first = CompleteAsync(controller, state);
        (await CompleteAsync(controller, state)).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        pending.SetResult(new("access", null, Guid.NewGuid(), null, Guid.NewGuid()));
        (await first).Should().BeOfType<NoContentResult>();
        await test.Api.Received(1).ExchangeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetOrDisconnectInvalidatesAnExchangeAlreadyInFlight(bool disconnect)
    {
        using var test = new NotionTestContext();
        var controller = test.Controller();
        var state = await AuthorizeAsync(controller);
        var pending = new TaskCompletionSource<NotionCredentials>();
        test.Api.ExchangeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(pending.Task);
        var exchange = CompleteAsync(controller, state);
        if (disconnect) await test.Connections.DisconnectAsync(test.User.Id, CancellationToken.None);
        else await new Orbit.Infrastructure.Persistence.AccountResetRepository(test.Db,
            Substitute.For<Orbit.Domain.Events.IAccountEventCollector>()).DeleteAllUserDataAsync(test.User.Id);
        pending.SetResult(new("access", null, Guid.NewGuid(), null, Guid.NewGuid()));
        (await exchange).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        (await test.Db.NotionConnections.AnyAsync()).Should().BeFalse();
        await test.Api.Received(1).RevokeAsync("access", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailedExchangeBurnsState()
    {
        using var test = new NotionTestContext();
        var controller = test.Controller();
        var state = await AuthorizeAsync(controller);
        test.Api.ExchangeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<NotionCredentials>(_ => throw new NotionApiException(400));
        (await CompleteAsync(controller, state)).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        (await CompleteAsync(controller, state)).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        await test.Api.Received(1).ExchangeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        (await test.Db.NotionConnections.AnyAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryEndpointReturnsExistingGateErrorWhenFlagIsOffOrProEnds(bool free)
    {
        using var test = new NotionTestContext();
        if (free)
        {
            var user = await test.Db.Users.SingleAsync();
            user.StartTrial(DateTime.UtcNow.AddDays(-1));
            await test.Db.SaveChangesAsync();
            test.Db.ChangeTracker.Clear();
        }
        else test.Flags.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>(), true).Returns(Array.Empty<string>());
        var controller = test.Controller();
        var responses = new[]
        {
            await controller.AuthorizeNotion(new(NotionTestContext.RedirectUri), CancellationToken.None),
            await CompleteAsync(controller, new string('a', 64)),
            await controller.GetNotion(CancellationToken.None),
            await controller.DisconnectNotion(CancellationToken.None)
        };
        foreach (var response in responses)
            response.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        (await test.Db.NotionOAuthStates.AnyAsync()).Should().BeFalse();
        await test.Api.DidNotReceive().ExchangeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AuthorizeRejectsUnlistedRedirectWithoutStoringState()
    {
        using var test = new NotionTestContext();
        var response = await test.Controller().AuthorizeNotion(new("https://attacker.example/callback"), CancellationToken.None);
        response.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        (await test.Db.NotionOAuthStates.AnyAsync()).Should().BeFalse();
    }

    internal static async Task<string> AuthorizeAsync(ConnectionsController controller)
    {
        var result = await controller.AuthorizeNotion(new(NotionTestContext.RedirectUri), CancellationToken.None);
        var payload = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<NotionAuthorizationResponse>().Subject;
        var url = new Uri(payload.AuthorizeUrl);
        url.Host.Should().Be("api.notion.com");
        var query = QueryHelpers.ParseQuery(url.Query);
        query["owner"].ToString().Should().Be("user");
        query["response_type"].ToString().Should().Be("code");
        query["redirect_uri"].ToString().Should().Be(NotionTestContext.RedirectUri);
        return query["state"].ToString();
    }

    private static Task<IActionResult> CompleteAsync(ConnectionsController controller, string state) =>
        controller.CompleteNotion(new("code", state, NotionTestContext.RedirectUri), CancellationToken.None);
}
