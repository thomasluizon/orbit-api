using Microsoft.AspNetCore.Http;
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

    internal static void Bind(HttpContext context, params string[] states)
    {
        context.Request.Headers.Cookie = string.Join("; ", states.Select(state =>
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(state)));
            return $"orbit_oauth_{hash}={hash}";
        }));
    }
}
