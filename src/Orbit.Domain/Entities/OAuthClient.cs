using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public sealed class OAuthClient : Entity
{
    public const int MaxNameLength = 200;
    public string ClientName { get; private set; } = string.Empty;
    public string[] RedirectUris { get; private set; } = [];
    public DateTime CreatedAtUtc { get; private set; }

    private OAuthClient() { }

    public static OAuthClient Create(string clientName, string[] redirectUris)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        ArgumentNullException.ThrowIfNull(redirectUris);
        if (clientName.Length > MaxNameLength || redirectUris.Length is 0 or > 50)
            throw new ArgumentException("A client name and between one and fifty redirect URIs are required.");
        if (redirectUris.Any(value => !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0))
            throw new ArgumentException("Redirect URIs must be HTTPS with no query, fragment or userinfo.");
        return new OAuthClient { ClientName = clientName, RedirectUris = redirectUris.Distinct(StringComparer.Ordinal).ToArray(), CreatedAtUtc = DateTime.UtcNow };
    }
}
