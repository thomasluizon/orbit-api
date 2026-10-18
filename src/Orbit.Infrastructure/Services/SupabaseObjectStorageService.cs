using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Common;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed partial class SupabaseObjectStorageService(
    IHttpClientFactory httpClientFactory,
    IOptions<SupabaseStorageSettings> options,
    ILogger<SupabaseObjectStorageService> logger) : IObjectStorageService, IObjectStorageDeletionService
{
    public const string HttpClientName = "SupabaseStorage";

    private readonly SupabaseStorageSettings _settings = options.Value;

    public async Task<SignedUpload> CreateSignedUploadAsync(string objectKey, string contentType, long sizeBytes, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);

        var requestPath = $"/storage/v1/object/upload/sign/{_settings.Bucket}/{objectKey}";
        using var response = await HttpRetryPolicy.SendWithRetryAsync(
            () => client.PostAsync(requestPath, content: null, cancellationToken),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            LogSignRequestFailed(logger, (int)response.StatusCode, _settings.Bucket, objectKey);
            throw new InvalidOperationException(
                $"Supabase Storage sign request failed ({(int)response.StatusCode}): {body}");
        }

        var payload = await response.Content.ReadFromJsonAsync<SignResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Supabase Storage sign response was empty.");

        if (string.IsNullOrWhiteSpace(payload.Url))
            throw new InvalidOperationException("Supabase Storage sign response did not contain a signed URL.");

        var baseUrl = _settings.Url.TrimEnd('/');
        var signedUrl = $"{baseUrl}/storage/v1{payload.Url}";
        var publicUrl = $"{baseUrl}/storage/v1/object/public/{_settings.Bucket}/{objectKey}";

        return new SignedUpload(objectKey, signedUrl, publicUrl);
    }

    public async Task DeleteUserObjectsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("A user id is required.", nameof(userId));
        ArgumentException.ThrowIfNullOrWhiteSpace(_settings.Bucket);

        var client = httpClientFactory.CreateClient(HttpClientName);
        var prefix = $"{userId}/";
        HashSet<string>? previousKeys = null;
        while (true)
        {
            using var listing = await HttpRetryPolicy.SendWithRetryAsync(
                () => client.PostAsJsonAsync($"/storage/v1/object/list-v2/{_settings.Bucket}",
                    new { prefix, limit = 1000, with_delimiter = false }, cancellationToken), cancellationToken);
            listing.EnsureSuccessStatusCode();
            var page = await listing.Content.ReadFromJsonAsync<ObjectListResponse>(cancellationToken)
                ?? throw new InvalidOperationException("Supabase Storage list response was empty.");
            var keys = page.Objects.Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
            if (keys.Count == 0)
                return;
            if (keys.Any(key => !key.StartsWith(prefix, StringComparison.Ordinal)))
                throw new InvalidOperationException("Storage returned an object outside the user's prefix.");
            if (previousKeys is not null && previousKeys.SetEquals(keys))
                throw new InvalidOperationException("Storage deletion left the same objects behind.");

            using var deletion = await HttpRetryPolicy.SendWithRetryAsync(async () =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, $"/storage/v1/object/{_settings.Bucket}")
                {
                    Content = JsonContent.Create(new { prefixes = keys }),
                };
                return await client.SendAsync(request, cancellationToken);
            }, cancellationToken);
            deletion.EnsureSuccessStatusCode();
            previousKeys = keys;
        }
    }

    private sealed record ObjectListResponse(
        [property: JsonPropertyName("objects"), JsonRequired] List<StoredObject> Objects);

    private sealed record StoredObject([property: JsonPropertyName("name"), JsonRequired] string Name);

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Supabase Storage sign request failed with status {StatusCode} for bucket {Bucket} key {ObjectKey}")]
    private static partial void LogSignRequestFailed(ILogger logger, int statusCode, string bucket, string objectKey);

    private sealed record SignResponse([property: JsonPropertyName("url")] string? Url);
}
