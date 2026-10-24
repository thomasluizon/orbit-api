using Orbit.Domain.Common;

namespace Orbit.Domain.Models;

public sealed class OAuthClientMetadata
{
    public const int MaxClientIdLength = 1024;
    public const int MaxClientNameLength = 200;
    public string ClientId { get; }
    public string ClientName { get; }
    public IReadOnlyList<string> RedirectUris { get; }

    private OAuthClientMetadata(string clientId, string clientName, IReadOnlyList<string> redirectUris)
    {
        ClientId = clientId;
        ClientName = clientName;
        RedirectUris = Array.AsReadOnly(redirectUris.ToArray());
    }

    public static Result<OAuthClientMetadata> Create(string? clientId, string? clientName,
        IReadOnlyList<string>? redirectUris, string? tokenEndpointAuthMethod = null)
    {
        if (!IsMetadataUrl(clientId) || string.IsNullOrWhiteSpace(clientName)
            || clientName.Length > MaxClientNameLength || redirectUris is null || redirectUris.Count is < 1 or > 50
            || redirectUris.Any(uri => !Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
                || parsed.Scheme is not ("https" or "http") || parsed.UserInfo.Length != 0 || parsed.Fragment.Length != 0)
            || tokenEndpointAuthMethod is not (null or "none"))
            return Result.Failure<OAuthClientMetadata>("Invalid OAuth client metadata.");
        return Result.Success(new OAuthClientMetadata(clientId!, clientName, redirectUris));
    }

    public static bool IsMetadataUrl(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > MaxClientIdLength
            || clientId.Any(char.IsWhiteSpace) || clientId.Contains('\\')
            || !Uri.TryCreate(clientId, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            return false;
        var suffix = clientId[(clientId.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var authorityEnd = suffix.IndexOfAny(['/', '?', '#']);
        if (authorityEnd < 0 || suffix[authorityEnd] != '/')
            return false;
        var path = suffix[authorityEnd..].Split('?', '#')[0];
        return !Uri.UnescapeDataString(path).Split('/').Any(segment => segment is "." or "..");
    }
}
