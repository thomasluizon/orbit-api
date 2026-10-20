using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Orbit.Infrastructure.Services.Notion;

internal sealed record NotionCredentials(string AccessToken, string? RefreshToken,
    Guid WorkspaceId, string? WorkspaceName, Guid BotId);

internal sealed class NotionApiException(int statusCode) : Exception("Notion request failed.")
{
    public int StatusCode { get; } = statusCode;
}

internal interface INotionApi
{
    Task<NotionCredentials> ExchangeAsync(string code, string redirectUri, CancellationToken ct);
    Task RevokeAsync(string token, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> SearchAsync(string token, CancellationToken ct);
    Task<JsonElement> GetDatabaseAsync(string token, Guid databaseId, CancellationToken ct);
    IAsyncEnumerable<JsonElement> QueryAsync(string token, Guid dataSourceId, CancellationToken ct);
}

internal sealed class NotionApi(HttpClient http, IOptions<NotionSettings> settings,
    NotionRequestScheduler scheduler, INotionDelay delay) : INotionApi
{
    private const string Version = "2026-03-11";

    public async Task<NotionCredentials> ExchangeAsync(string code, string redirectUri, CancellationToken ct)
    {
        var response = await SendAsync("oauth/token", new { grant_type = "authorization_code", code, redirect_uri = redirectUri }, null, ct);
        return new NotionCredentials(response.GetProperty("access_token").GetString()!,
            response.GetProperty("refresh_token").GetString(), response.GetProperty("workspace_id").GetGuid(),
            response.GetProperty("workspace_name").GetString(), response.GetProperty("bot_id").GetGuid());
    }

    public async Task RevokeAsync(string token, CancellationToken ct) =>
        await SendAsync("oauth/revoke", new { token }, null, ct);

    public Task<JsonElement> GetDatabaseAsync(string token, Guid databaseId, CancellationToken ct) =>
        SendAsync($"databases/{databaseId:D}", null, token, ct);

    public async Task<IReadOnlyList<JsonElement>> SearchAsync(string token, CancellationToken ct)
    {
        var sources = new List<JsonElement>();
        await foreach (var source in PaginateAsync("search", token,
            new Dictionary<string, object?> { ["filter"] = new { property = "object", value = "data_source" } }, ct))
            sources.Add(source);
        return sources;
    }

    public IAsyncEnumerable<JsonElement> QueryAsync(string token, Guid dataSourceId, CancellationToken ct) =>
        PaginateAsync($"data_sources/{dataSourceId:D}/query", token,
            new Dictionary<string, object?> { ["result_type"] = "page" }, ct);

    private async IAsyncEnumerable<JsonElement> PaginateAsync(string endpoint, string token,
        Dictionary<string, object?> body, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        body["page_size"] = 100;
        do
        {
            var response = await SendAsync(endpoint, body, token, ct);
            foreach (var result in response.GetProperty("results").EnumerateArray())
                yield return result.Clone();
            if (!response.GetProperty("has_more").GetBoolean()) yield break;
            body["start_cursor"] = response.GetProperty("next_cursor").GetString()
                ?? throw new JsonException("Notion pagination cursor is missing.");
        } while (true);
    }

    private Task<JsonElement> SendAsync(string endpoint, object? body, string? token, CancellationToken ct) =>
        scheduler.RunAsync(token ?? settings.Value.ClientId, async () =>
        {
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post,
                    new Uri($"https://api.notion.com/v1/{endpoint}"));
                request.Headers.Add("Notion-Version", Version);
                request.Headers.Authorization = token is not null
                    ? new AuthenticationHeaderValue("Bearer", token)
                    : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Value.ClientId}:{settings.Value.ClientSecret}")));
                if (body is not null) request.Content = JsonContent.Create(body);
                using var response = await http.SendAsync(request, ct);
                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode == 529)
                {
                    var error = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                    if (error.TryGetProperty("additional_data", out var extra)
                        && extra.TryGetProperty("rate_limit_reason", out var reason)
                        && reason.GetString() == "public_api_request_blocked")
                        throw new NotionApiException((int)response.StatusCode);
                    var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.Zero;
                    var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt) + Random.Shared.NextDouble());
                    await delay.WaitAsync(retryAfter > backoff ? retryAfter : backoff, ct);
                    if (attempt >= 3) throw new NotionApiException((int)response.StatusCode);
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw new NotionApiException((int)response.StatusCode);
                return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            }
        }, ct);
}
