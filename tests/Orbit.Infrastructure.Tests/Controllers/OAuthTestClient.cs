using Microsoft.AspNetCore.Http;
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

    internal static void Bind(HttpContext context, params string[] states)
    {
        context.Request.Headers.Cookie = string.Join("; ", states.Select(state =>
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(state)));
            return $"orbit_oauth_{hash}={hash}";
        }));
    }
}
