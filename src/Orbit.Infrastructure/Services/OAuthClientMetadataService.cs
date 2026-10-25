using System.Net;
using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Auth.Models;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

public sealed class OAuthClientMetadataService(IHttpClientFactory httpClients, IMemoryCache cache,
    IValidator<OAuthClientMetadataDocument> validator, TimeProvider timeProvider) : IOAuthClientMetadataService
{
    public const string HttpClientName = "OAuthClientMetadata";
    public const int MaxDocumentBytes = 5 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<OAuthClientMetadata?> GetAsync(string clientId, CancellationToken cancellationToken)
    {
        if (!OAuthClientMetadata.TryParseMetadataUrl(clientId, out var metadataUri))
            return null;
        var cacheKey = (HttpClientName, clientId);
        if (cache.TryGetValue<OAuthClientMetadata>(cacheKey, out var cached))
            return cached;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        var ct = deadline.Token;
        try
        {
            using var client = httpClients.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, metadataUri);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!IsAcceptableResponse(response))
                return null;
            var body = await ReadBoundedBodyAsync(response.Content, ct);
            if (body is null)
                return null;
            using var json = JsonDocument.Parse(body.Value);
            var metadata = ValidateDocument(json.RootElement, clientId);
            if (metadata is null)
                return null;
            var lifetime = CacheLifetime(response);
            if (lifetime > TimeSpan.Zero)
                cache.Set(cacheKey, metadata, lifetime);
            return metadata;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static bool IsAcceptableResponse(HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        return response.StatusCode == HttpStatusCode.OK && !(response.Content.Headers.ContentLength > MaxDocumentBytes)
            && mediaType is not null && (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || mediaType.StartsWith("application/", StringComparison.OrdinalIgnoreCase)
                && mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<ReadOnlyMemory<byte>?> ReadBoundedBodyAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxDocumentBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0)
                break;
            length += read;
        }
        return length > MaxDocumentBytes ? null : buffer.AsMemory(0, length);
    }

    private OAuthClientMetadata? ValidateDocument(JsonElement root, string clientId)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        var properties = root.EnumerateObject().Select(property => property.Name).ToArray();
        if (properties.Distinct(StringComparer.Ordinal).Count() != properties.Length
            || properties.Contains("client_secret", StringComparer.Ordinal)
            || properties.Contains("client_secret_expires_at", StringComparer.Ordinal))
            return null;
        var document = root.Deserialize<OAuthClientMetadataDocument>();
        if (document is null || document.ClientId != clientId || !validator.Validate(document).IsValid)
            return null;
        var metadata = OAuthClientMetadata.Create(document.ClientId, document.ClientName,
            document.RedirectUris, document.TokenEndpointAuthMethod);
        return metadata.IsFailure ? null : metadata.Value;
    }

    private TimeSpan CacheLifetime(HttpResponseMessage response)
    {
        var control = response.Headers.CacheControl;
        if (control is { NoStore: true } or { NoCache: true } or { Private: true })
            return TimeSpan.Zero;
        var now = timeProvider.GetUtcNow();
        var date = response.Headers.Date ?? now;
        var age = response.Headers.Age ?? TimeSpan.Zero;
        if (now - date > age)
            age = now - date;
        var lifetime = (control?.SharedMaxAge ?? control?.MaxAge
            ?? (response.Content.Headers.Expires - date) ?? TimeSpan.Zero) - age;
        return lifetime > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : lifetime;
    }
}
