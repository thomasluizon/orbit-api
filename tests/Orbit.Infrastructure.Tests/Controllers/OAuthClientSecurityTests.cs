using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.OAuth;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class OAuthClientSecurityTests : IDisposable
{
    private const string RedirectUri = "https://claude.ai/api/mcp/auth_callback";
    private readonly SqliteOrbitDbContextFactory _db = new();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly OAuthController _controller;
    private readonly OAuthAuthorizationStore _store;

    public OAuthClientSecurityTests()
    {
        _store = new OAuthAuthorizationStore(new OAuthStateStore(_db.Context, TimeProvider.System), TimeProvider.System);
        OAuthTestClient.RegisterWith(_mediator, new GenericRepository<OAuthClient>(_db.Context), new UnitOfWork(_db.Context, new DatabaseConnectionSettings()));
        _controller = new OAuthController(_mediator, _store,
            Substitute.For<IGenericRepository<ApiKey>>(), new UnitOfWork(_db.Context, new DatabaseConnectionSettings()),
            Options.Create(new GoogleSettings()), new ConfigurationBuilder().Build(),
            NullLogger<OAuthController>.Instance, Substitute.For<IOAuthClientMetadataService>(), new GenericRepository<OAuthClient>(_db.Context))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [Theory]
    [InlineData(RedirectUri)]
    [InlineData("https://claude.ai/anything")]
    public async Task Authorize_UnknownClient_FailsClosed(string redirectUri)
    {
        var result = await _controller.Authorize(new OAuthController.AuthorizeRequest
        {
            ClientId = "x",
            RedirectUri = redirectUri,
            State = "state",
            ResponseType = "code",
            CodeChallenge = "challenge",
            CodeChallengeMethod = "S256"
        });
        Error(result).Should().Be("invalid_client");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsentPost_MissingBrowserCookie_DoesNotConsumeTicket(bool cancel)
    {
        var ticket = _store.CreatePendingConsent(new ConsentEntry(Guid.NewGuid(), "challenge", RedirectUri,
            "client", "state", null, "en", ClientName: "Claude"));
        var result = cancel ? _controller.CancelConsent(ticket) : _controller.GrantConsent(ticket, "requested");
        Error(result).Should().Be("invalid_request");
        _store.ReadPendingConsent(ticket)!.IsConsumed.Should().BeFalse();
    }

    private static string? Error(IActionResult result) =>
        JsonSerializer.SerializeToElement(result.Should().BeOfType<BadRequestObjectResult>().Subject.Value)
            .GetProperty("error").GetString();

    public void Dispose() => _db.Dispose();
}
