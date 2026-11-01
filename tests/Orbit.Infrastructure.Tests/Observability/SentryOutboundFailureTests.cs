using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Extensions;
using Orbit.Api.Observability;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Sentry;
using Sentry.AspNetCore;

namespace Orbit.Infrastructure.Tests.Observability;

[CollectionDefinition("Sentry SDK", DisableParallelization = true)]
public class SentrySdkCollection;

[Collection("Sentry SDK")]
public class SentryOutboundFailureTests
{
    [Fact]
    public async Task HandledModerationFailure_ReportsOneWarningAndPreservesOtherOutboundFailures()
    {
        var reported = new List<SentryEvent>();
        using var sdk = SentrySdk.Init(options =>
        {
            options.Dsn = "https://key@localhost/1";
            options.Transport = Substitute.For<Sentry.Extensibility.ITransport>();
            options.SetBeforeSend((sentryEvent, hint) =>
            {
                var filtered = SentryEventScrubber.Scrub(sentryEvent, hint);
                if (filtered is not null)
                    reported.Add(filtered);
                return null;
            });
        });
        using var http = new HttpClient(new SentryHttpMessageHandler(new FailedResponseHandler()));
        var logger = Substitute.For<ILogger<ContentModerationService>>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);
        using var services = new ServiceCollection().AddScoped(_ => Substitute.For<IFeatureFlagService>()).BuildServiceProvider();
        var moderation = new ContentModerationService(http,
            Options.Create(new AiSettings { ApiKey = "test-key", BaseUrl = "https://api.openai.com/v1" }), logger,
            services.GetRequiredService<IServiceScopeFactory>());

        var result = await moderation.CheckTextAsync("test text");

        result.Unavailable.Should().BeTrue();
        reported.Should().BeEmpty();
        logger.Received(1).Log(LogLevel.Warning, Arg.Is<EventId>(id => id.Id == 1), Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception?>(), Arg.Any<Func<Arg.AnyType, Exception?, string>>());

        using var other = await http.GetAsync("https://api.openai.com/v1/responses");
        reported.Should().ContainSingle().Which.Exception.Should().BeOfType<HttpRequestException>();

        SentrySdk.CaptureException(new HttpRequestException("unhandled outbound failure"));
        reported.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("01abcdef23456789abcdef0123456789abcdef0123")]
    [InlineData("98abcdef23456789abcdef0123456789abcdef0123")]
    public void AddOrbitObservability_UsesDeployedCommitForRelease(string commit)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["RENDER_GIT_COMMIT"] = commit });
        builder.AddOrbitObservability();
        using var provider = builder.Services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<SentryAspNetCoreOptions>>().Value;

        options.Release.Should().Be($"Orbit.Api@{commit}");
    }

    private sealed class FailedResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { RequestMessage = request });
    }
}
