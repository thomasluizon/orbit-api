using System.ClientModel.Primitives;
using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Tests.AI;

public class AiEvalEndpointIsolationTests
{
    [Theory]
    [InlineData("audio", true, true, false)]
    [InlineData("moderation", true, true, false)]
    [InlineData("audio", false, true, false)]
    [InlineData("moderation", false, true, false)]
    [InlineData("audio", true, false, false)]
    [InlineData("moderation", true, false, false)]
    [InlineData("audio", false, true, true)]
    [InlineData("moderation", false, true, true)]
    [InlineData("audio", false, false, true)]
    [InlineData("moderation", false, false, true)]
    public async Task AuthenticatedRequests_SelectProjectBeforeAnyBudgetContext(string kind, bool listed, bool configured, bool flagged)
    {
        var userId = Guid.NewGuid();
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        http.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["StagingEval:AccountIds"] = (listed ? userId : Guid.NewGuid()).ToString()
        }).Build();
        var options = Options.Create(new AiSettings
        {
            ApiKey = "production-test-key",
            EvalApiKey = configured ? "eval-test-key" : "",
            BaseUrl = "https://orbit.test/v1",
            MaxRetries = 0
        });
        var handler = new RejectedRequestCapture();
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(flagged ? [FeatureFlagKeys.AstraEvalUnmetered] : []);
        var eval = listed || flagged;
        if (kind == "audio")
        {
            var budget = Substitute.For<IAiDailyBudget>();
            var service = new AudioTranscriptionService(options, NullLogger<AudioTranscriptionService>.Instance,
                new HttpClientPipelineTransport(new HttpClient(handler)), flags, config, http, budget);
            using var audio = AudioTranscriptionBudgetTests.Wave();
            (await service.TranscribeAsync(audio, "audio.wav", "en")).IsFailure.Should().BeTrue();
            if (eval)
                await budget.Received(1).ReserveAsync(userId, 200, Arg.Any<CancellationToken>());
            else
                await budget.DidNotReceiveWithAnyArgs().ReserveAsync(default, default, default);
        }
        else
        {
            var service = new ContentModerationService(new HttpClient(handler), options,
                NullLogger<ContentModerationService>.Instance, flags, config, http);
            if (eval && !configured)
            {
                var check = () => service.CheckTextAsync("hello");
                await check.Should().ThrowAsync<InvalidOperationException>();
            }
            else
                (await service.CheckTextAsync("hello")).Unavailable.Should().BeTrue();
        }

        if (eval && !configured)
            handler.Credentials.Should().BeEmpty();
        else
            handler.Credentials.Should().Equal(eval ? "eval-test-key" : "production-test-key");
    }

    private sealed class RejectedRequestCapture : HttpMessageHandler
    {
        public List<string?> Credentials { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Credentials.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}") });
        }
    }
}
