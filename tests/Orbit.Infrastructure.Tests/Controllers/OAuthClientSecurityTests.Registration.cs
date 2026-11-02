using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Orbit.Api.Controllers;
using Orbit.Api.RateLimiting;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class OAuthClientSecurityTests
{
    [Theory]
    [InlineData("https://claude.ai/anything")]
    [InlineData("https://claude.ai:8443/api/mcp/auth_callback")]
    [InlineData("https://claude.ai/api/mcp/auth_callback?query=1")]
    [InlineData("https://claude.ai/api/mcp/auth_callback#fragment")]
    [InlineData("https://user@claude.ai/api/mcp/auth_callback")]
    [InlineData("https://claude.com/api/mcp/auth_callback")]
    public async Task Register_UnlistedUri_IsRejectedWithoutPersisting(string uri)
    {
        (await _controller.Register(JsonSerializer.SerializeToElement(new { client_name = "Claude", redirect_uris = new[] { uri } })))
            .Should().BeOfType<BadRequestObjectResult>();
        (await _db.Context.OAuthClients.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RegisteredClient_ExactRedirectAndBrowserCookie_AreRequired()
    {
        var registration = (await _controller.Register(JsonSerializer.SerializeToElement(new { client_name = "Claude", redirect_uris = new[] { RedirectUri } })))
            .Should().BeOfType<ObjectResult>().Subject;
        var clientId = JsonSerializer.SerializeToElement(registration.Value).GetProperty("client_id").GetString()!;
        (await _db.Context.OAuthClients.AsNoTracking().SingleAsync()).RedirectUris.Should().Equal(RedirectUri);
        var request = new OAuthController.AuthorizeRequest
        {
            ClientId = clientId,
            RedirectUri = RedirectUri,
            State = "state",
            ResponseType = "code",
            CodeChallenge = "challenge",
            CodeChallengeMethod = "S256"
        };
        (await _controller.Authorize(request)).Should().BeOfType<ContentResult>();
        var cookie = _controller.Response.Headers.SetCookie.Single()!;
        cookie.Should().Contain("secure").And.Contain("httponly").And.Contain("samesite=lax").And.Contain("path=/oauth").And.Contain("max-age=1800");
        _controller.Request.Headers.Cookie = cookie.Split(';')[0];
        var ticket = _store.CreatePendingConsent(new(Guid.NewGuid(), "challenge", RedirectUri, clientId,
            "state", null, "en", ClientName: "Claude", BrowserBindingHash: OAuthTestClient.BindingHash(_controller.HttpContext, "state")));
        var page = _controller.Consent(ticket).Should().BeOfType<ContentResult>().Subject;
        page.Content.Should().Contain("Claude · claude.ai");
        _controller.Request.Headers.Cookie = cookie.Split(';')[0] + "tampered";
        Error(_controller.Consent(ticket)).Should().Be("invalid_request");
        _controller.Request.Headers.Cookie = cookie.Split(';')[0];
        _controller.GrantConsent(ticket, "requested").Should().BeOfType<RedirectResult>();
    }

    [Theory]
    [InlineData("Register")]
    [InlineData("Consent")]
    [InlineData("GrantConsent")]
    [InlineData("CancelConsent")]
    public void WritingAndConsentEndpoints_HaveDistributedRateLimit(string method) =>
        typeof(OAuthController).GetMethod(method)!.GetCustomAttribute<DistributedRateLimitAttribute>().Should().NotBeNull();
}
