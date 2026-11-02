using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Microsoft.AspNetCore.Http;
using Orbit.Api.Controllers;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Models;
using Orbit.Domain.Entities;
using Orbit.Api.OAuth;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class OAuthControllerTests
{
    [Fact]
    public async Task Token_AuthorizationCodeOlderThanFiveMinutes_ReturnsInvalidGrant()
    {
        var (verifier, challenge) = GeneratePkce();
        var code = _authStore.CreateCode(UserId, challenge, OAuthTestClient.RedirectUri, OAuthTestClient.ClientId);
        _timeProvider.Advance(TimeSpan.FromMinutes(6));
        AssertInvalidGrant(await _controller.Token("authorization_code", code, verifier, OAuthTestClient.RedirectUri,
            default, client_id: OAuthTestClient.ClientId));
        await AssertNoKeyAdded();
    }

    [Fact]
    public async Task Token_RefreshFamilyExpired_ReturnsInvalidGrant()
    {
        var token = _authStore.CreateRefreshToken(new(UserId, Guid.NewGuid(), OAuthTestClient.ClientId,
            Guid.NewGuid(), _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(-1)));
        AssertInvalidGrant(await _controller.Token("refresh_token", null, null, null, default, token, OAuthTestClient.ClientId));
        await AssertNoKeyAdded();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Token_RefreshWithMissingOrForeignPreviousKey_ReturnsInvalidGrant(bool foreign)
    {
        var key = ApiKey.Create(Guid.NewGuid(), "Other", ["read_habits"]).Value.Entity;
        var token = _authStore.CreateRefreshToken(new(UserId, key.Id, OAuthTestClient.ClientId));
        _apiKeyRepo.GetByIdAsync(key.Id, Arg.Any<CancellationToken>()).Returns(foreign ? key : null);
        AssertInvalidGrant(await _controller.Token("refresh_token", null, null, null, default, token, OAuthTestClient.ClientId));
        await _apiKeyRepo.DidNotReceive().FindOneTrackedAsync(Arg.Any<Expression<Func<ApiKey, bool>>>(),
            Arg.Any<Func<IQueryable<ApiKey>, IQueryable<ApiKey>>?>(), Arg.Any<CancellationToken>());
        await AssertNoKeyAdded();
    }

    [Fact]
    public async Task Token_AllowedButDifferentRedirectUri_ReturnsInvalidGrant()
    {
        var (verifier, challenge) = GeneratePkce();
        var code = _authStore.CreateCode(UserId, challenge, OAuthTestClient.RedirectUri, OAuthTestClient.ClientId);
        AssertInvalidGrant(await _controller.Token("authorization_code", code, verifier, "https://claude.com/callback",
            default, client_id: OAuthTestClient.ClientId));
        await AssertNoKeyAdded();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Token_RefreshDuringMetadataOutage_UsesPersistedClientBinding(bool foreignClient)
    {
        var key = ApiKey.Create(UserId, "Claude.ai", ["read_habits"], oauthFamilyId: Guid.NewGuid(),
            oauthClientId: OAuthTestClient.ClientId).Value.Entity;
        var token = _authStore.CreateRefreshToken(new(UserId, key.Id, OAuthTestClient.ClientId, key.OAuthFamilyId!.Value));
        _apiKeyRepo.GetByIdAsync(key.Id, Arg.Any<CancellationToken>()).Returns(key);
        _apiKeyRepo.FindOneTrackedAsync(Arg.Any<Expression<Func<ApiKey, bool>>>(),
            Arg.Any<Func<IQueryable<ApiKey>, IQueryable<ApiKey>>?>(), Arg.Any<CancellationToken>()).Returns(key);
        _clientMetadata.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((OAuthClientMetadata?)null);
        _clientMetadata.ClearReceivedCalls();
        var result = await _controller.Token("refresh_token", null, null, null, default, token,
            foreignClient ? "https://other.example.com/client" : OAuthTestClient.ClientId);
        if (foreignClient) AssertInvalidGrant(result);
        else result.Should().BeOfType<OkObjectResult>();
        await _clientMetadata.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        if (foreignClient) await AssertNoKeyAdded();
    }

    [Fact]
    public async Task VerifyCode_Success_RenewsBrowserCookieForConsent()
    {
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LoginResponse(UserId, "token", "User", "user@example.com")));
        _timeProvider.Advance(TimeSpan.FromMinutes(4));
        _controller.Response.Headers.Remove("Set-Cookie");
        var response = (await _controller.VerifyCode(new("user@example.com", "123456", "state", "challenge", OAuthTestClient.RedirectUri,
            OAuthTestClient.ClientId), default)).Should().BeOfType<OkObjectResult>().Subject;
        _controller.Response.Headers.SetCookie.Should().ContainSingle().Which.Should().Contain("max-age=300");
        _timeProvider.Advance(TimeSpan.FromMinutes(4));
        var url = JsonSerializer.SerializeToElement(response.Value).GetProperty("redirectUrl").GetString()!;
        var ticket = System.Web.HttpUtility.ParseQueryString(new Uri("https://api.example.com" + url).Query)["ticket"]!;
        _controller.Consent(ticket).Should().BeOfType<ContentResult>();
        _controller.GrantConsent(ticket, "requested").Should().BeOfType<RedirectResult>();
    }

    [Fact]
    public async Task GoogleCallback_Success_RenewsOriginalBrowserCookie()
    {
        var pending = CreatePendingGoogleRequest();
        _mediator.Send(Arg.Any<GoogleCodeAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LoginResponse(UserId, "token", "User", "user@example.com")));
        var original = _controller.Request.Headers.Cookie.ToString();
        _timeProvider.Advance(TimeSpan.FromMinutes(4));
        var response = (await _controller.GoogleCallback(pending.GoogleState, "code", null, default))
            .Should().BeOfType<RedirectResult>().Subject;
        _controller.Response.Headers.SetCookie.Should().ContainSingle().Which.Should().Contain("max-age=300");
        original.Should().Contain(_controller.Response.Headers.SetCookie.Single()!.Split(';')[0]);
        _timeProvider.Advance(TimeSpan.FromMinutes(4));
        var ticket = System.Web.HttpUtility.ParseQueryString(new Uri("https://api.example.com" + response.Url).Query)["ticket"]!;
        _controller.Consent(ticket).Should().BeOfType<ContentResult>();
        _controller.GrantConsent(ticket, "requested").Should().BeOfType<RedirectResult>();
    }

    [Fact]
    public async Task GoogleCallback_SecondBrowserAuthorizesSameState_RejectsBeforeProviderExchange()
    {
        var pending = CreatePendingGoogleRequest();
        _controller.ControllerContext = new() { HttpContext = new DefaultHttpContext() };
        await _controller.Authorize(new OAuthController.AuthorizeRequest
        {
            ClientId = OAuthTestClient.ClientId,
            RedirectUri = OAuthTestClient.RedirectUri,
            State = pending.ClientState,
            ResponseType = "code",
            CodeChallenge = pending.CodeChallenge,
            CodeChallengeMethod = "S256"
        });
        _controller.Request.Headers.Cookie = _controller.Response.Headers.SetCookie.Last()!.Split(';')[0];
        var response = (await _controller.GoogleCallback(pending.GoogleState, "code", null, default))
            .Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.SerializeToElement(response.Value).GetProperty("error").GetString().Should().Be("invalid_request");
        await _mediator.DidNotReceive().Send(Arg.Any<GoogleCodeAuthCommand>(), Arg.Any<CancellationToken>());
    }

    private static void AssertInvalidGrant(IActionResult result) => JsonSerializer.SerializeToElement(
        result.Should().BeOfType<BadRequestObjectResult>().Subject.Value).GetProperty("error").GetString().Should().Be("invalid_grant");

    private Task AssertNoKeyAdded() => _apiKeyRepo.DidNotReceive().AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
}
