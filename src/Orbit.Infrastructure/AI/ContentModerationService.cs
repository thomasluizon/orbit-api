using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Orbit.Infrastructure.AI;

public partial class ContentModerationService(
    HttpClient httpClient,
    IOptions<AiSettings> aiSettings,
    ILogger<ContentModerationService> logger,
    IServiceScopeFactory scopeFactory,
    IConfiguration? configuration = null,
    IHttpContextAccessor? http = null) : IContentModerationService
{
    public static readonly HttpRequestOptionsKey<bool> HandlesFailedResponse = new("Orbit.ContentModeration.HandlesFailedResponse");
    private const string ModerationModel = "omni-moderation-latest";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<ModerationResult> CheckTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var settings = aiSettings.Value;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.BaseUrl.TrimEnd('/')}/moderations")
            {
                Content = JsonContent.Create(new ModerationRequest(ModerationModel, text), options: SerializerOptions)
            };
            var account = await ResolveAccountAsync(cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AiEvalCredentials.Resolve(settings, account));
            request.Options.Set(HandlesFailedResponse, true);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogModerationUnavailable(logger, (int)response.StatusCode);
                return Unavailable;
            }

            var payload = await response.Content.ReadFromJsonAsync<ModerationResponse>(SerializerOptions, cancellationToken);
            var result = payload?.Results is { Count: > 0 } results ? results[0] : null;
            if (result is null)
                return Unavailable;

            var flaggedCategories = result.Categories?
                .Where(category => category.Value)
                .Select(category => category.Key)
                .ToList() ?? [];

            return new ModerationResult(result.Flagged, Unavailable: false, flaggedCategories);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
        {
            LogModerationFailed(logger, exception);
            return Unavailable;
        }
    }

    private async Task<AiEvalAccount> ResolveAccountAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        return await AiEvalAccount.ResolveAsync(AiEvalAccount.CurrentUserId(http),
            scope.ServiceProvider.GetRequiredService<IFeatureFlagService>(), configuration, cancellationToken);
    }

    private static ModerationResult Unavailable => new(false, true, []);

    private sealed record ModerationRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string Input);

    private sealed record ModerationResponse(
        [property: JsonPropertyName("results")] IReadOnlyList<ModerationResultPayload>? Results);

    private sealed record ModerationResultPayload(
        [property: JsonPropertyName("flagged")] bool Flagged,
        [property: JsonPropertyName("categories")] Dictionary<string, bool>? Categories);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Content moderation returned non-success status {StatusCode}; treating as unavailable")]
    private static partial void LogModerationUnavailable(ILogger logger, int statusCode);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Content moderation call failed; treating as unavailable")]
    private static partial void LogModerationFailed(ILogger logger, Exception exception);
}
