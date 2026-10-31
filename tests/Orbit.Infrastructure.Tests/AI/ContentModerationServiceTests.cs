using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;

namespace Orbit.Infrastructure.Tests.AI;

public class ContentModerationServiceTests
{
    private readonly StubHttpMessageHandler _handler = new();
    private readonly ContentModerationService _sut;

    public ContentModerationServiceTests()
    {
        var httpClient = new HttpClient(_handler)
        {
            BaseAddress = new Uri("https://api.openai.com/v1")
        };
        var aiSettings = Options.Create(new AiSettings
        {
            ApiKey = "test-key",
            BaseUrl = "https://api.openai.com/v1"
        });
        _sut = new ContentModerationService(httpClient, aiSettings, NullLogger<ContentModerationService>.Instance);
    }

    [Fact]
    public async Task CheckTextAsync_EvalAccountUsesEvalProjectBeforeCompletionContextExists()
    {
        var userId = Guid.NewGuid();
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        http.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"));
        using var services = new ServiceCollection().AddSingleton<IHttpContextAccessor>(http)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["StagingEval:AccountIds"] = userId.ToString() }).Build()).BuildServiceProvider();
        var service = ActivatorUtilities.CreateInstance<ContentModerationService>(services, new HttpClient(_handler),
            Options.Create(new AiSettings { ApiKey = "production-test-key", EvalApiKey = "eval-test-key" }), NullLogger<ContentModerationService>.Instance);
        _handler.Response = new(HttpStatusCode.BadRequest);

        await service.CheckTextAsync("any text");

        _handler.Credential.Should().Be("eval-test-key");
    }

    [Fact]
    public async Task CheckTextAsync_FlaggedResponse_ReturnsOnlyTrueCategories()
    {
        _handler.Response = JsonResponse(HttpStatusCode.OK, """
            {"results":[{"flagged":true,"categories":{"harassment":true,"sexual":false}}]}
            """);

        var result = await _sut.CheckTextAsync("borderline text");

        result.Flagged.Should().BeTrue();
        result.Unavailable.Should().BeFalse();
        result.Categories.Should().Contain("harassment");
        result.Categories.Should().NotContain("sexual");
    }

    [Fact]
    public async Task CheckTextAsync_NotFlaggedResponse_ReturnsCleanAvailableResult()
    {
        _handler.Response = JsonResponse(HttpStatusCode.OK, """
            {"results":[{"flagged":false,"categories":{"harassment":false,"violence":false}}]}
            """);

        var result = await _sut.CheckTextAsync("a kind note");

        result.Flagged.Should().BeFalse();
        result.Unavailable.Should().BeFalse();
        result.Categories.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckTextAsync_NonSuccessStatus_ReturnsUnavailableWithoutThrowing()
    {
        _handler.Response = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var result = await _sut.CheckTextAsync("any text");

        result.Unavailable.Should().BeTrue();
        result.Flagged.Should().BeFalse();
    }

    [Fact]
    public async Task CheckTextAsync_HttpRequestException_ReturnsUnavailableWithoutThrowing()
    {
        _handler.ExceptionToThrow = new HttpRequestException("connection refused");

        var result = await _sut.CheckTextAsync("any text");

        result.Unavailable.Should().BeTrue();
        result.Flagged.Should().BeFalse();
    }

    [Fact]
    public async Task CheckTextAsync_TimeoutWhileCallerTokenLive_ReturnsUnavailableWithoutThrowing()
    {
        _handler.ExceptionToThrow = new TaskCanceledException("the request timed out");

        var result = await _sut.CheckTextAsync("any text", CancellationToken.None);

        result.Unavailable.Should().BeTrue();
        result.Flagged.Should().BeFalse();
    }

    [Fact]
    public async Task CheckTextAsync_CallerCancelsToken_RethrowsOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = async () => await _sut.CheckTextAsync("any text", cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body) =>
        new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public string? Credential { get; private set; }
        public HttpResponseMessage? Response { get; set; }
        public Exception? ExceptionToThrow { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Credential = request.Headers.Authorization?.Parameter;

            if (ExceptionToThrow is not null)
                throw ExceptionToThrow;

            return Task.FromResult(Response ?? new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
