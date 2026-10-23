using System.Text.Json;
using FluentAssertions;
using Hangfire;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.OAuth;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Auth.Services;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Tests.Controllers;

public class OAuthAppleControllerTests
{
    private readonly IAppleIdentityService _apple = Substitute.For<IAppleIdentityService>();
    private readonly IOAuthStateStore _states = Substitute.For<IOAuthStateStore>();
    private readonly OAuthAuthorizationStore _store;
    private readonly OAuthController _controller;
    private readonly AppleAuthorizationFlow _flow;
    private readonly Dictionary<(string Key, string Kind), string> _entries = [];
    private const string Redirect = "https://claude.ai/api/mcp/auth_callback";

    public OAuthAppleControllerTests()
    {
        _states.Put(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<int?>()).Returns(call =>
        { _entries[(call.ArgAt<string>(0), call.ArgAt<string>(1))] = call.ArgAt<string>(2); return true; });
        _states.Take(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>()).Returns(call =>
            _entries.Remove((call.ArgAt<string>(0), call.ArgAt<string>(1)), out var value) ? value : null);
        var users = Substitute.For<IGenericRepository<User>>();
        var userStore = Substitute.For<IAuthUserStore>();
        userStore.UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<User>());
        var work = Substitute.For<IUnitOfWork>();
        work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<LoginResponse>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<Result<LoginResponse>>>>()(default));
        var sessions = Substitute.For<IAuthSessionService>();
        sessions.CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new SessionTokens("access", "refresh")));
        var signIn = new AppleSignInFlow(users, userStore, work, sessions, Substitute.For<ISignupJobDispatcher>(), Substitute.For<IBackgroundJobClient>(),
            new AccountReactivation(Substitute.For<IGenericRepository<OwedSubscriptionCancellation>>()));
        _apple.IsConfigured.Returns(true);
        _apple.AuthenticateWebAsync("code", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AppleIdentity("subject", "apple@example.com", true, "apple-refresh", "web"));
        _flow = new(_states, _apple, signIn, Options.Create(new AppleSignInSettings
        {
            KeyId = "key",
            TeamId = "team",
            PrivateKey = "key",
            NativeClientId = "native",
            WebClientId = "web",
            WebOrigin = "https://app.example.com",
            RedirectUri = "https://api.example.com/api/auth/apple/callback",
            McpRedirectUri = "https://api.example.com/oauth/apple/callback"
        }), TimeProvider.System);
        _store = new(_states, TimeProvider.System);
        _controller = new(Substitute.For<IMediator>(), _store, Substitute.For<IGenericRepository<ApiKey>>(), work,
            Options.Create(new GoogleSettings()), new ConfigurationBuilder().Build(), NullLogger<OAuthController>.Instance)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
    }

    [Fact]
    public async Task StartAndCallback_PreserveConsentContextAndRejectReplay()
    {
        var start = (RedirectResult)_controller.AppleStart(_flow, "client", Redirect, "client-state", "challenge", "S256", "nonce", AgentScopes.ReadHabits);
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(start.Url).Query)["state"]!;
        var result = await _controller.AppleCallback(_flow, new() { State = state, Code = "code" }, default);
        ((StatusCodeResult)result).StatusCode.Should().Be(StatusCodes.Status303SeeOther);
        var location = _controller.Response.Headers.Location.ToString();
        location.Should().StartWith("/oauth/consent?ticket=");
        var ticket = location.Split('=')[1];
        var consent = _store.ConsumePendingConsent(ticket)!;
        consent.UserId.Should().NotBeEmpty();
        consent.ClientId.Should().Be("client");
        consent.RedirectUri.Should().Be(Redirect);
        consent.CodeChallenge.Should().Be("challenge");
        consent.State.Should().Be("client-state");
        consent.Nonce.Should().Be("nonce");
        _controller.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        _controller.Response.Headers.SetCookie.Should().BeEmpty();
        (await _controller.AppleCallback(_flow, new() { State = state, Code = "code" }, default)).Should().BeOfType<UnauthorizedResult>();
    }

    [Theory]
    [InlineData("", Redirect, "state", "challenge", "S256")]
    [InlineData("client", "https://attacker.example.com/callback", "state", "challenge", "S256")]
    [InlineData("client", Redirect, "", "challenge", "S256")]
    [InlineData("client", Redirect, "state", "", "S256")]
    [InlineData("client", Redirect, "state", "challenge", "plain")]
    public void Start_InvalidAuthorizationRequest_Rejects(string client, string redirect, string state, string challenge, string method)
    {
        _controller.AppleStart(_flow, client, redirect, state, challenge, method).Should().BeOfType<BadRequestResult>();
        _entries.Should().BeEmpty();
    }

    [Fact]
    public void Start_InvalidScopeOrDisabledProvider_Rejects()
    {
        _controller.AppleStart(_flow, "client", Redirect, "state", "challenge", "S256", scope: "unsupported")
            .Should().BeOfType<BadRequestObjectResult>();
        _apple.IsConfigured.Returns(false);
        ((StatusCodeResult)_controller.AppleStart(_flow, "client", Redirect, "state", "challenge", "S256"))
            .StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("missing-code")]
    [InlineData("state-length")]
    [InlineData("code-length")]
    [InlineData("user-length")]
    public async Task Callback_InvalidForm_RejectsBeforeExchange(string failure)
    {
        var request = new AuthController.AppleCallbackRequest
        {
            State = failure == "state-length" ? new string('s', 257) : "state",
            Code = failure == "missing-code" ? null : failure == "code-length" ? new string('c', 4097) : "code",
            Error = failure == "error" ? "access_denied" : null,
            User = failure == "user-length" ? new string('u', 16_385) : null
        };
        (await _controller.AppleCallback(_flow, request, default)).Should().BeOfType<UnauthorizedResult>();
        await _apple.DidNotReceive().AuthenticateWebAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("redirect")]
    public async Task Callback_InvalidStoredConsent_Rejects(string failure)
    {
        var start = (RedirectResult)_controller.AppleStart(_flow, "client", Redirect, "state", "challenge", "S256");
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(start.Url).Query)["state"]!;
        var key = (state, "apple-authorization");
        var request = JsonSerializer.Deserialize<AppleAuthorizationRequest>(_entries[key])!;
        var context = failure == "null" ? "null" : JsonSerializer.Serialize(new ConsentEntry(Guid.Empty, "challenge", "https://attacker.example.com/callback", "client", "state", null, "en", []));
        _entries[key] = JsonSerializer.Serialize(request with { McpContext = context });
        (await _controller.AppleCallback(_flow, new() { State = state, Code = "code" }, default)).Should().BeOfType<UnauthorizedResult>();
    }
}
