using System.Text.Json.Serialization;

namespace Orbit.Application.Auth.Models;

public sealed record OAuthClientMetadataDocument(
    [property: JsonPropertyName("client_id")] string? ClientId,
    [property: JsonPropertyName("client_name")] string? ClientName,
    [property: JsonPropertyName("redirect_uris")] string[]? RedirectUris,
    [property: JsonPropertyName("token_endpoint_auth_method")] string? TokenEndpointAuthMethod);
