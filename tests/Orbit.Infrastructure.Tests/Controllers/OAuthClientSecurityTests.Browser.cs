using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Validators;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class OAuthClientSecurityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("wrong-cookie")]
    public async Task VerifyCode_MissingOrMismatchedBrowserBinding_RejectsBeforeSignIn(string? cookie)
    {
        var client = OAuthClient.Create("Claude", [RedirectUri]);
        _db.Context.Add(client);
        await _db.Context.SaveChangesAsync();
        if (cookie is not null) _controller.Request.Headers.Cookie = cookie;
        var result = await _controller.VerifyCode(new OAuthController.VerifyCodeRequest("user@example.com", "123456",
            "state", "challenge", RedirectUri, client.Id.ToString()), default);
        Error(result).Should().Be("invalid_request");
        await _mediator.DidNotReceive().Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GoogleCallback_MissingBrowserBinding_RejectsBeforeProviderExchange()
    {
        var client = OAuthClient.Create("Claude", [RedirectUri]);
        _db.Context.Add(client);
        await _db.Context.SaveChangesAsync();
        var pending = _store.TryCreateGoogleRequest(client.Id.ToString(), RedirectUri, "state", "challenge", null,
            "https://api.useorbit.org/oauth/google/callback", "en")!;
        Error(await _controller.GoogleCallback(pending.GoogleState, "code", null, default)).Should().Be("invalid_request");
        await _mediator.DidNotReceive().Send(Arg.Any<GoogleCodeAuthCommand>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback")]
    [InlineData("https://claude.ai/anything")]
    public async Task UnknownClient_IsRejectedAcrossSignInAndTokenEndpoints(string redirect)
    {
        Error(await _controller.VerifyCode(new("user@example.com", "123456", "state", "challenge", redirect, "x"), default)).Should().Be("invalid_client");
        Error(await _controller.GoogleStart("x", redirect, "state", "challenge", "S256")).Should().Be("invalid_client");
        Error(await _controller.Token("authorization_code", "code", "verifier", redirect, default, client_id: "x")).Should().Be("invalid_client");
        _mediator.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public void PendingGoogleRequests_EnforcePerIpCapacityWithoutEviction()
    {
        var first = _store.TryCreateGoogleRequest("client", RedirectUri, "state", "challenge", null, "https://api.example.com/callback", "en", clientIp: "first-ip")!;
        for (var i = 1; i < 20; i++)
            _store.TryCreateGoogleRequest("client", RedirectUri, "state", "challenge", null, "https://api.example.com/callback", "en", clientIp: "first-ip").Should().NotBeNull();
        _store.TryCreateGoogleRequest("client", RedirectUri, "state", "challenge", null, "https://api.example.com/callback", "en", clientIp: "first-ip").Should().BeNull();
        _store.TryCreateGoogleRequest("client", RedirectUri, "state", "challenge", null, "https://api.example.com/callback", "en", clientIp: "second-ip").Should().NotBeNull();
        _store.ConsumeGoogleRequest(first.GoogleState).Should().NotBeNull();
    }

    [Theory]
    [InlineData("", "https://claude.ai/api/mcp/auth_callback")]
    [InlineData("Claude", "https://claude.ai/api/mcp/auth_callback?query=1")]
    [InlineData("Claude", "http://claude.ai/api/mcp/auth_callback")]
    public void Registration_HasValidatorAndDomainGuards(string name, string uri)
    {
        var validator = new OAuthClientRegistrationValidator();
        validator.Validate(new RegisterOAuthClientCommand(name, [uri])).IsValid.Should().BeFalse();
        var create = () => OAuthClient.Create(name, [uri]);
        create.Should().Throw<ArgumentException>();
    }
}
