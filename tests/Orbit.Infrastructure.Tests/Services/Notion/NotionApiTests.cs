using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Orbit.Infrastructure.Services.Notion;

namespace Orbit.Infrastructure.Tests.Services.Notion;

public class NotionApiTests
{
    [Fact]
    public async Task ExchangeUsesDocumentedBasicAuthAndAcceptsNullableRefreshToken()
    {
        using var http = new NotionHttpStub();
        http.Responses.Enqueue(NotionHttpStub.Json(new
        {
            access_token = "access",
            refresh_token = (string?)null,
            bot_id = Guid.NewGuid(),
            workspace_icon = (string?)null,
            workspace_name = (string?)null,
            workspace_id = Guid.NewGuid(),
            owner = new { type = "workspace", workspace = true },
            duplicated_template_id = (string?)null,
            token_type = "bearer"
        }));
        var credentials = await http.Api.ExchangeAsync("code", NotionTestContext.RedirectUri, CancellationToken.None);
        credentials.AccessToken.Should().Be("access");
        credentials.RefreshToken.Should().BeNull();
        credentials.WorkspaceName.Should().BeNull();
        var request = http.Requests.Single();
        request.Path.Should().Be("/v1/oauth/token");
        request.Authorization.Should().Be("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("client:secret")));
        request.Version.Should().Be("2026-03-11");
        var body = JsonSerializer.Deserialize<JsonElement>(request.Body!);
        body.GetProperty("grant_type").GetString().Should().Be("authorization_code");
        body.GetProperty("redirect_uri").GetString().Should().Be(NotionTestContext.RedirectUri);
    }

    [Fact]
    public async Task SearchPaginatesSharedDataSourcesAndUsesBearerToken()
    {
        using var http = new NotionHttpStub();
        var cursor = Guid.NewGuid().ToString();
        http.Responses.Enqueue(NotionHttpStub.List([NotionExamples.Source()], true, cursor));
        http.Responses.Enqueue(NotionHttpStub.List([NotionExamples.Source()], false));
        (await http.Api.SearchAsync("access", CancellationToken.None)).Should().HaveCount(2);
        var first = JsonSerializer.Deserialize<JsonElement>(http.Requests[0].Body!);
        first.GetProperty("filter").GetProperty("value").GetString().Should().Be("data_source");
        first.GetProperty("page_size").GetInt32().Should().Be(100);
        JsonSerializer.Deserialize<JsonElement>(http.Requests[1].Body!).GetProperty("start_cursor").GetString().Should().Be(cursor);
        http.Requests.Should().OnlyContain(r => r.Authorization == "Bearer access" && r.Path == "/v1/search");
        http.Delays.Should().OnlyContain(d => d >= TimeSpan.FromMilliseconds(350));
    }

    [Fact]
    public async Task QueryPaginatesPagesAndDatabaseRetrievalUsesGet()
    {
        using var http = new NotionHttpStub();
        http.Responses.Enqueue(NotionHttpStub.List([NotionExamples.Element(NotionExamples.Page())], true, Guid.NewGuid().ToString()));
        http.Responses.Enqueue(NotionHttpStub.List([], false));
        http.Responses.Enqueue(NotionHttpStub.Json(NotionExamples.Source()));
        var pages = new List<JsonElement>();
        await foreach (var page in http.Api.QueryAsync("access", NotionExamples.SourceId, CancellationToken.None)) pages.Add(page);
        pages.Should().HaveCount(1);
        await http.Api.GetDatabaseAsync("access", NotionExamples.DatabaseId, CancellationToken.None);
        http.Requests[0].Path.Should().Be($"/v1/data_sources/{NotionExamples.SourceId}/query");
        http.Requests[2].Method.Should().Be("GET");
        http.Requests[2].Path.Should().Be($"/v1/databases/{NotionExamples.DatabaseId}");
    }

    [Theory]
    [InlineData(429)]
    [InlineData(529)]
    public async Task ThrottledRequestsWaitForFullRetryAfterBeforeRetry(int statusCode)
    {
        using var http = new NotionHttpStub();
        http.Responses.Enqueue(NotionHttpStub.Throttled(statusCode, 73));
        http.Responses.Enqueue(NotionHttpStub.List([], false));
        await http.Api.SearchAsync("access", CancellationToken.None);
        http.Delays.Should().Contain(TimeSpan.FromSeconds(73));
        http.Events.Should().ContainInOrder("request", "wait:73", "request");
        http.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task RepeatedThrottlingHasABoundedRetryCountAndHonorsEveryWait()
    {
        using var http = new NotionHttpStub();
        for (var i = 0; i < 4; i++) http.Responses.Enqueue(NotionHttpStub.Throttled(429, 73));
        var act = () => http.Api.SearchAsync("access", CancellationToken.None);
        (await act.Should().ThrowAsync<NotionApiException>()).Which.StatusCode.Should().Be(429);
        http.Requests.Should().HaveCount(4);
        http.Delays.Count(d => d == TimeSpan.FromSeconds(73)).Should().Be(4);
    }

    [Fact]
    public async Task RevokeUsesDocumentedEndpointAndTokenBody()
    {
        using var http = new NotionHttpStub();
        http.Responses.Enqueue(NotionHttpStub.Json(new { request_id = Guid.NewGuid() }));
        await http.Api.RevokeAsync("access", CancellationToken.None);
        var request = http.Requests.Single();
        request.Path.Should().Be("/v1/oauth/revoke");
        request.Authorization.Should().StartWith("Basic ");
        JsonSerializer.Deserialize<JsonElement>(request.Body!).GetProperty("token").GetString().Should().Be("access");
    }
}

internal sealed class NotionHttpStub : HttpMessageHandler
{
    internal sealed record Request(string Path, string Method, string? Authorization, string? Version, string? Body);
    internal readonly Queue<HttpResponseMessage> Responses = new();
    internal readonly List<Request> Requests = [];
    internal readonly List<TimeSpan> Delays = [];
    internal readonly List<string> Events = [];
    internal readonly INotionApi Api;
    private readonly HttpClient _http;

    internal NotionHttpStub()
    {
        _http = new HttpClient(this, disposeHandler: false);
        var delay = new RecordingDelay(Delays, Events);
        Api = new NotionApi(_http, Options.Create(new NotionSettings { ClientId = "client", ClientSecret = "secret" }),
            new NotionRequestScheduler(delay), delay);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Events.Add("request");
        Requests.Add(new(request.RequestUri!.AbsolutePath, request.Method.Method, request.Headers.Authorization?.ToString(),
            request.Headers.GetValues("Notion-Version").Single(),
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
        return Responses.Dequeue();
    }

    internal static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    internal static HttpResponseMessage List(JsonElement[] values, bool more, string? cursor = null) =>
        Json(new { @object = "list", results = values, has_more = more, next_cursor = cursor });

    internal static HttpResponseMessage Throttled(int code, int seconds)
    {
        var response = Json(new { @object = "error", code = code == 429 ? "rate_limited" : "service_overload", status = code, message = "Request rate exceeded." });
        response.StatusCode = (HttpStatusCode)code;
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return response;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _http.Dispose();
        base.Dispose(disposing);
    }

    private sealed class RecordingDelay(List<TimeSpan> delays, List<string> events) : INotionDelay
    {
        public Task WaitAsync(TimeSpan delay, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            delays.Add(delay);
            events.Add($"wait:{delay.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            return Task.CompletedTask;
        }
    }
}
