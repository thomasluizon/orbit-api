using Microsoft.AspNetCore.Http;
using Orbit.Api.OAuth;
using MediatR;
using Orbit.Domain.Entities;
using Orbit.Application.Auth.Commands;
using NSubstitute;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Controllers;

internal static class OAuthTestClient
{
    internal const string ClientId = "https://claude.ai/oauth/mcp-oauth-client-metadata";
    internal const string RedirectUri = "https://claude.ai/api/mcp/auth_callback";

    internal static IOAuthClientMetadataService Metadata()
    {
        var service = Substitute.For<IOAuthClientMetadataService>();
        service.GetAsync(ClientId, Arg.Any<CancellationToken>()).Returns(
            OAuthClientMetadata.Create(ClientId, "Claude", [RedirectUri], "none").Value);
        return service;
    }

    internal static void RegisterWith(IMediator mediator, IGenericRepository<OAuthClient> clients, IUnitOfWork work)
    {
        var handler = new RegisterOAuthClientCommandHandler(clients, work);
        mediator.Send(Arg.Any<RegisterOAuthClientCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<RegisterOAuthClientCommand>(), call.Arg<CancellationToken>()));
    }

    internal static string CookieName(string state) => "orbit_oauth_" + Hash(state);

    internal static string? BindingHash(HttpContext context, string state) =>
        context.Request.Cookies[CookieName(state)] is { } secret ? Hash(secret) : null;

    private static string Hash(string value) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    internal static string Consent(OAuthAuthorizationStore store, HttpContext context, ConsentEntry entry) =>
        store.CreatePendingConsent(entry with { BrowserBindingHash = BindingHash(context, entry.State) });

    internal static void Bind(HttpContext context, OAuthAuthorizationStore store, params string[] states)
    {
        var cookies = states.Select(state =>
        {
            var existing = context.Request.Cookies[CookieName(state)];
            var secret = existing is not null && store.ReadBrowserBinding(existing) == state
                ? existing : store.CreateBrowserBinding(state);
            return $"{CookieName(state)}={secret}";
        }).ToArray();
        context.Request.Headers.Cookie = string.Join("; ", cookies);
    }
}
