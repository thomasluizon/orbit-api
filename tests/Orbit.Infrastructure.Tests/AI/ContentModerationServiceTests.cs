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
using NSubstitute;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.AI;

public class ContentModerationServiceTests : IDisposable
{
    private readonly StubHttpMessageHandler _handler = new();
    private readonly ContentModerationService _sut;
    private readonly ServiceProvider _services = new ServiceCollection()
        .AddScoped(_ => Substitute.For<IFeatureFlagService>()).BuildServiceProvider();

    public void Dispose() => _services.Dispose();

    public ContentModerationServiceTests()
    {
        var httpClient = new HttpClient(_handler)
        {
            BaseAddress = new Uri("https://api.openai.com/v1"),
            Timeout = ContentModerationService.RequestDeadline
        };
        var aiSettings = Options.Create(new AiSettings
        {
            ApiKey = "test-key",
            BaseUrl = "https://api.openai.com/v1"
        });
        _sut = new ContentModerationService(httpClient, aiSettings, NullLogger<ContentModerationService>.Instance,
            _services.GetRequiredService<IServiceScopeFactory>());
    }

    [Fact]
    public async Task CheckTextAsync_EvalAccountUsesEvalProjectBeforeCompletionContextExists()
    {
        var userId = Guid.NewGuid();
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        http.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"));
        using var services = new ServiceCollection().AddSingleton<IHttpContextAccessor>(http)
            .AddSingleton(Substitute.For<IFeatureFlagService>())
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

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task CheckTextAsync_NonSuccessStatus_ReturnsUnavailableWithoutThrowing(HttpStatusCode statusCode)
    {
        _handler.Response = new HttpResponseMessage(statusCode);

        var result = await _sut.CheckTextAsync("any text");

        result.Unavailable.Should().BeTrue();
        result.Flagged.Should().BeFalse();
        _handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task CheckTextAsync_HttpRequestException_ReturnsUnavailableWithoutThrowing()
    {
        _handler.ExceptionToThrow = new HttpRequestException("connection refused");

        var result = await _sut.CheckTextAsync("any text");

        result.Unavailable.Should().BeTrue();
        result.Flagged.Should().BeFalse();
        _handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task CheckTextAsync_TimeoutWhileCallerTokenLive_ReturnsUnavailableWithoutThrowing()
    {
        _handler.ExceptionToThrow = new TaskCanceledException("the request timed out");

        var result = await _sut.CheckTextAsync("any text", CancellationToken.None);

        result.Unavailable.Should().BeTrue();
        result.Flagged.Should().BeFalse();
        _handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task CheckTextAsync_StalledProviderStopsWithinTheTurnBudget()
    {
        _handler.Stall = true;

        var result = await _sut.CheckTextAsync("any text").WaitAsync(TimeSpan.FromSeconds(5));

        result.Unavailable.Should().BeTrue();
        result.Flagged.Should().BeFalse();
        _handler.Calls.Should().Be(2);
        _handler.CancelledCalls.Should().Be(2);
    }

    [Fact]
    public async Task CheckTextAsync_StalledFirstAttempt_RecoversOnSecondAttempt()
    {
        _handler.Stall = true;
        _handler.FailuresBeforeRecovery = 1;
        _handler.Response = JsonResponse(HttpStatusCode.OK, """
            {"results":[{"flagged":false,"categories":{}}]}
            """);

        var result = await _sut.CheckTextAsync("any text").WaitAsync(TimeSpan.FromSeconds(5));

        result.Unavailable.Should().BeFalse();
        result.Flagged.Should().BeFalse();
        _handler.Calls.Should().Be(2);
        _handler.CancelledCalls.Should().Be(1);
    }

    [Fact]
    public async Task CheckTextAsync_TransportFailure_RecoversOnSecondAttempt()
    {
        _handler.ExceptionToThrow = new HttpRequestException("connection reset");
        _handler.FailuresBeforeRecovery = 1;
        _handler.Response = JsonResponse(HttpStatusCode.OK, """
            {"results":[{"flagged":true,"categories":{"harassment":true}}]}
            """);

        var result = await _sut.CheckTextAsync("any text");

        result.Unavailable.Should().BeFalse();
        result.Flagged.Should().BeTrue();
        result.Categories.Should().Equal("harassment");
        _handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task CheckTextAsync_InvalidJson_ReturnsUnavailableWithoutRetrying()
    {
        _handler.Response = JsonResponse(HttpStatusCode.OK, "invalid json");

        var result = await _sut.CheckTextAsync("any text");

        result.Unavailable.Should().BeTrue();
        _handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task CheckTextAsync_CallerCancelsDuringStall_DoesNotRetry()
    {
        _handler.Stall = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = async () => await _sut.CheckTextAsync("any text", cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _handler.Calls.Should().Be(1);
        _handler.CancelledCalls.Should().Be(1);
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
        public bool Stall { get; set; }
        public int FailuresBeforeRecovery { get; set; } = int.MaxValue;
        public int CancelledCalls { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            Credential = request.Headers.Authorization?.Parameter;

            if (Stall && Calls <= FailuresBeforeRecovery)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                finally { if (cancellationToken.IsCancellationRequested) CancelledCalls++; }
            }

            if (ExceptionToThrow is not null && Calls <= FailuresBeforeRecovery)
                throw ExceptionToThrow;

            return Response ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
