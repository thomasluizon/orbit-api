using Orbit.Domain.Models;

namespace Orbit.Api.Controllers;

public partial class OAuthController
{
    private async Task<(string? ClientName, string? Error)> ValidateClientAsync(string clientId, string? redirectUri,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > OAuthClientMetadata.MaxClientIdLength)
            return (null, "invalid_client");
        var identity = await ResolveClientIdentityAsync(clientId, ct);
        if (identity is null || string.IsNullOrWhiteSpace(identity.ClientName))
            return (null, "invalid_client");
        return redirectUri is null || (IsRedirectUriAllowed(redirectUri)
            && identity.RedirectUris.Contains(redirectUri, StringComparer.Ordinal))
            ? (identity.ClientName, null) : (null, "invalid_redirect_uri");
    }

    private async Task<ClientIdentity?> ResolveClientIdentityAsync(string clientId, CancellationToken ct)
    {
        if (clientId.Contains(':') || clientId.StartsWith("//", StringComparison.Ordinal))
        {
            var metadata = await clientMetadata.GetAsync(clientId, ct);
            return metadata is null ? null : new(metadata.ClientName, metadata.RedirectUris);
        }

        if (!Guid.TryParse(clientId, out var id))
            return null;
        var client = await clients.GetByIdAsync(id, ct);
        return client is null || !string.Equals(client.Id.ToString(), clientId, StringComparison.Ordinal)
            ? null : new(client.ClientName, client.RedirectUris);
    }

    private sealed record ClientIdentity(string ClientName, IReadOnlyList<string> RedirectUris);
}
