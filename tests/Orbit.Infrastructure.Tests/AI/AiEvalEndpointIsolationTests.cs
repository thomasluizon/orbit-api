using System.ClientModel.Primitives;
using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Tests.AI;

public class AiEvalEndpointIsolationTests
{
    [Theory]
    [InlineData("audio", true, true)]
    [InlineData("moderation", true, true)]
    [InlineData("audio", false, true)]
    [InlineData("moderation", false, true)]
    [InlineData("audio", true, false)]
    [InlineData("moderation", true, false)]
    public async Task AuthenticatedRequests_SelectProjectBeforeAnyBudgetContext(string kind, bool eval, bool configured)
    {
        var userId = Guid.NewGuid();
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        http.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["StagingEval:AccountIds"] = (eval ? userId : Guid.NewGuid()).ToString()
        }).Build();
        var options = Options.Create(new AiSettings
        {
            ApiKey = "production-test-key",
            EvalApiKey = configured ? "eval-test-key" : "",
            BaseUrl = "https://orbit.test/v1",
            MaxRetries = 0
        });
        var handler = new RejectedRequestCapture();
        if (kind == "audio")
        {
            var service = new AudioTranscriptionService(options, NullLogger<AudioTranscriptionService>.Instance,
                new HttpClientPipelineTransport(new HttpClient(handler)), config, http);
            using var audio = new MemoryStream([0, 1]);
            (await service.TranscribeAsync(audio, "audio.wav", "en")).IsFailure.Should().BeTrue();
        }
        else
        {
            var service = new ContentModerationService(new HttpClient(handler), options,
                NullLogger<ContentModerationService>.Instance, config, http);
            (await service.CheckTextAsync("hello")).Unavailable.Should().BeTrue();
        }

        handler.Credentials.Should().Equal(eval && configured ? "eval-test-key" : "production-test-key");
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
