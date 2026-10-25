using Orbit.Domain.Models;

namespace Orbit.Api.Controllers;

public partial class OAuthController
{
    private async Task<(string? ClientName, string? Error)> ValidateClientAsync(string clientId, string redirectUri,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > OAuthClientMetadata.MaxClientIdLength)
            return (null, "invalid_client");
        if (!clientId.Contains(':') && !clientId.StartsWith("//", StringComparison.Ordinal))
            return (null, null);
        var metadata = await clientMetadata.GetAsync(clientId, ct);
        if (metadata is null)
            return (null, "invalid_client");
        return metadata.RedirectUris.Contains(redirectUri, StringComparer.Ordinal)
            ? (metadata.ClientName, null) : (null, "invalid_request");
    }
}
