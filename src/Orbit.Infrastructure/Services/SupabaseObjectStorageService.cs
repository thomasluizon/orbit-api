using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed class SupabaseObjectStorageService(
    IHttpClientFactory httpClientFactory,
    IOptions<SupabaseStorageSettings> options) : IObjectStorageService
{
    public const string HttpClientName = "SupabaseStorage";

    private readonly SupabaseStorageSettings _settings = options.Value;

    public async Task<SignedUpload> CreateSignedUploadAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);

        var requestPath = $"/storage/v1/object/upload/sign/{_settings.Bucket}/{objectKey}";
        using var response = await client.PostAsync(requestPath, content: null, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
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

    private sealed record SignResponse([property: JsonPropertyName("url")] string? Url);
}
