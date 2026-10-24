using System.Linq.Expressions;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.OAuth;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Auth.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;
using Orbit.Infrastructure.Tests.Services;

namespace Orbit.Infrastructure.Tests.Controllers;

public class OAuthCimdControllerTests : IDisposable
{
    private const string ClientId = OAuthClientMetadataServiceTests.ClientId;
    private const string RedirectUri = "https://claude.ai/api/mcp/auth_callback";
    private const string Verifier = "original-client-code-verifier-with-enough-entropy";
    private static readonly Guid UserId = Guid.NewGuid();
    private readonly SqliteOrbitDbContextFactory _db = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly DocumentHandler _handler = new();
    private readonly IHttpClientFactory _httpClients = Substitute.For<IHttpClientFactory>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IGenericRepository<ApiKey> _keys = Substitute.For<IGenericRepository<ApiKey>>();
    private readonly OAuthAuthorizationStore _store;
    private readonly OAuthController _controller;
    private ApiKey? _minted;

    public OAuthCimdControllerTests()
    {
        _httpClients.CreateClient(OAuthClientMetadataService.HttpClientName).Returns(_ => new HttpClient(_handler, false));
        var metadata = new OAuthClientMetadataService(_httpClients, _cache, new OAuthClientMetadataDocumentValidator(), TimeProvider.System);
        _store = new OAuthAuthorizationStore(new OAuthStateStore(_db.Context, TimeProvider.System), TimeProvider.System);
        var work = Substitute.For<IUnitOfWork>();
        work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<IActionResult>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<IActionResult>>>()(default));
        work.ExecuteReplayableInTransactionAsync(Arg.Any<Func<CancellationToken, Task<IActionResult>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<IActionResult>>>()(default));
        _keys.AddAsync(Arg.Do<ApiKey>(key => _minted = key), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _controller = new OAuthController(_mediator, _store, _keys, work,
            Options.Create(new GoogleSettings { ClientId = "google-client", AllowedRedirectUris = ["https://api.useorbit.org/oauth/google/callback"] }),
            new ConfigurationBuilder().Build(), NullLogger<OAuthController>.Instance, metadata)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        _controller.Request.Scheme = "https";
        _controller.Request.Host = new HostString("api.useorbit.org");
        var login = Result.Success(new LoginResponse(UserId, "login-token", "Alex", "alex@example.com"));
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>()).Returns(login);
        _mediator.Send(Arg.Any<GoogleCodeAuthCommand>(), Arg.Any<CancellationToken>()).Returns(login);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Client_CompletesAuthorizeConsentTokenAndRefresh(bool dcr)
    {
        var clientId = ClientId;
        if (dcr)
        {
            var registered = _controller.Register(JsonSerializer.SerializeToElement(new
            {
                client_name = "Claude",
                redirect_uris = new[] { RedirectUri }
            })).Should().BeOfType<ObjectResult>().Subject;
            registered.StatusCode.Should().Be(201);
            clientId = JsonSerializer.SerializeToElement(registered.Value).GetProperty("client_id").GetString()!;
        }
        (await Authorize(clientId)).Should().BeOfType<ContentResult>();
        var verified = await Verify(clientId);
        var ticket = Query(verified.Should().BeOfType<OkObjectResult>().Subject.Value!, "redirectUrl", "ticket");
        var consent = _controller.Consent(ticket).Should().BeOfType<ContentResult>().Subject;
        if (!dcr)
            consent.Content.Should().Contain("Claude · claude.ai");
        var granted = _controller.GrantConsent(ticket, "requested").Should().BeOfType<RedirectResult>().Subject;
        var code = QueryHelpers.ParseQuery(new Uri(granted.Url).Query)["code"].ToString();
        var token = (await _controller.Token("authorization_code", code, Verifier, RedirectUri, default, client_id: clientId))
            .Should().BeOfType<OkObjectResult>().Subject;
        _minted!.OAuthClientId.Should().Be(clientId);
        _minted.OAuthFamilyId.Should().NotBeNull();
        var old = _minted;
        var refresh = JsonSerializer.SerializeToElement(token.Value).GetProperty("refresh_token").GetString()!;
        _keys.GetByIdAsync(old.Id, Arg.Any<CancellationToken>()).Returns(old);
        _keys.FindOneTrackedAsync(Arg.Any<Expression<Func<ApiKey, bool>>>(),
            Arg.Any<Func<IQueryable<ApiKey>, IQueryable<ApiKey>>?>(), Arg.Any<CancellationToken>()).Returns(old);
        Error(await _controller.Token("refresh_token", null, null, null, default, refresh, "another-client"))
            .Should().Be("invalid_grant");
        (await _controller.Token("refresh_token", null, null, null, default, refresh, clientId)).Should().BeOfType<OkObjectResult>();
        _minted!.OAuthClientId.Should().Be(clientId);
        _minted.OAuthFamilyId.Should().Be(old.OAuthFamilyId);
        old.IsRevoked.Should().BeTrue();
        _handler.Requests.Should().Be(dcr ? 0 : 1);
    }

    [Fact]
    public async Task Google_UsesValidatedClientIdentityOnConsent()
    {
        var start = (await _controller.GoogleStart(ClientId, RedirectUri, "state", Challenge(), "S256"))
            .Should().BeOfType<RedirectResult>().Subject;
        var state = QueryHelpers.ParseQuery(new Uri(start.Url).Query)["state"].ToString();
        var callback = (await _controller.GoogleCallback(state, "google-code", null, default))
            .Should().BeOfType<RedirectResult>().Subject;
        var ticket = QueryHelpers.ParseQuery(new Uri("https://api.useorbit.org" + callback.Url).Query)["ticket"].ToString();
        _store.ReadPendingConsent(ticket)!.Entry.ClientName.Should().Be("Claude");
        var granted = _controller.GrantConsent(ticket, "requested").Should().BeOfType<RedirectResult>().Subject;
        var code = QueryHelpers.ParseQuery(new Uri(granted.Url).Query)["code"].ToString();
        (await _controller.Token("authorization_code", code, Verifier, RedirectUri, default, client_id: ClientId))
            .Should().BeOfType<OkObjectResult>();
        _minted!.OAuthClientId.Should().Be(ClientId);
    }

    [Theory]
    [InlineData("another-client")]
    [InlineData("https://claude.ai/oauth/mcp-oauth-client-metadata/")]
    public async Task Token_RejectsDifferentClientEvenWithCorrectPkce(string clientId)
    {
        var code = _store.CreateCode(UserId, Challenge(), RedirectUri, ClientId);
        Error(await _controller.Token("authorization_code", code, Verifier, RedirectUri, default, client_id: clientId))
            .Should().Be("invalid_grant");
        _minted.Should().BeNull();
    }

    [Fact]
    public async Task SignInEntryPoints_RejectUnlistedRedirectBeforeAuthentication()
    {
        const string unlisted = "https://claude.ai/unlisted";
        Error(await Authorize(ClientId, unlisted)).Should().Be("invalid_request");
        Error(await Verify(ClientId, unlisted)).Should().Be("invalid_request");
        Error(await _controller.GoogleStart(ClientId, unlisted, "state", Challenge(), "S256")).Should().Be("invalid_request");
        await _mediator.DidNotReceive().Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>());
        _db.Context.OAuthStates.Should().BeEmpty();
    }

    [Fact]
    public async Task Authorize_RejectsDocumentClientIdMismatch()
    {
        _handler.Body = _handler.Body.Replace(ClientId, ClientId + "/");
        Error(await Authorize()).Should().Be("invalid_client");
    }

    [Fact]
    public async Task Authorize_RejectsOversizedDocument()
    {
        _handler.Body += new string(' ', OAuthClientMetadataService.MaxDocumentBytes);
        Error(await Authorize()).Should().Be("invalid_client");
    }

    [Fact]
    public async Task Authorize_RejectsPrivateAddress()
    {
        using var handler = OAuthMetadataNetwork.CreateHandler();
        _httpClients.CreateClient(OAuthClientMetadataService.HttpClientName).Returns(_ => new HttpClient(handler, false));
        Error(await Authorize("https://127.0.0.1/client.json")).Should().Be("invalid_client");
    }

    [Fact]
    public async Task Consent_EncodesPublishedClientName()
    {
        _handler.Body = _handler.Body.Replace("\"Claude\"", "\"<script>alert(1)</script>\"");
        var ticket = Query(((OkObjectResult)await Verify(ClientId)).Value!, "redirectUrl", "ticket");
        var html = _controller.Consent(ticket).Should().BeOfType<ContentResult>().Subject.Content;
        html.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt;").And.NotContain("<script>alert(1)</script>");
    }

    private Task<IActionResult> Authorize(string clientId = ClientId, string redirectUri = RedirectUri) =>
        _controller.Authorize(new OAuthController.AuthorizeRequest
        {
            ClientId = clientId,
            RedirectUri = redirectUri,
            ResponseType = "code",
            State = "state",
            CodeChallenge = Challenge(),
            CodeChallengeMethod = "S256"
        });

    private Task<IActionResult> Verify(string clientId, string redirectUri = RedirectUri) =>
        _controller.VerifyCode(new OAuthController.VerifyCodeRequest("alex@example.com", "123456", "state",
            Challenge(), redirectUri, clientId), default);

    private static string Challenge() => WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)));
    private static string Error(IActionResult result) => JsonSerializer.SerializeToElement(
        result.Should().BeOfType<BadRequestObjectResult>().Subject.Value).GetProperty("error").GetString()!;
    private static string Query(object response, string property, string key) => QueryHelpers.ParseQuery(new Uri(
        "https://api.useorbit.org" + JsonSerializer.SerializeToElement(response).GetProperty(property).GetString()).Query)[key].ToString();

    public void Dispose()
    {
        _db.Dispose();
        _cache.Dispose();
        _handler.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class DocumentHandler : HttpMessageHandler
    {
        public string Body { get; set; } = OAuthClientMetadataServiceTests.ClaudeDocument;
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) };
            return Task.FromResult(response);
        }
    }
}
