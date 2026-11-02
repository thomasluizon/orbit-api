using Orbit.Domain.Models;

namespace Orbit.Api.Controllers;

public partial class OAuthController
{
    private async Task<(string? ClientName, string? Error)> ValidateClientAsync(string clientId, string? redirectUri,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > OAuthClientMetadata.MaxClientIdLength)
            return (null, "invalid_client");
        string? name;
        IReadOnlyList<string> uris;
        if (!clientId.Contains(':') && !clientId.StartsWith("//", StringComparison.Ordinal))
        {
            if (!Guid.TryParse(clientId, out var id))
                return (null, "invalid_client");
            var client = await clients.GetByIdAsync(id, ct);
            if (client is null || !string.Equals(client.Id.ToString(), clientId, StringComparison.Ordinal))
                return (null, "invalid_client");
            name = client.ClientName;
            uris = client.RedirectUris;
        }
        else
        {
            var metadata = await clientMetadata.GetAsync(clientId, ct);
            if (metadata is null)
                return (null, "invalid_client");
            name = metadata.ClientName;
            uris = metadata.RedirectUris;
        }
        if (string.IsNullOrWhiteSpace(name))
            return (null, "invalid_client");
        return redirectUri is null || (IsRedirectUriAllowed(redirectUri) && uris.Contains(redirectUri, StringComparer.Ordinal))
            ? (name, null) : (null, "invalid_redirect_uri");
    }
}
