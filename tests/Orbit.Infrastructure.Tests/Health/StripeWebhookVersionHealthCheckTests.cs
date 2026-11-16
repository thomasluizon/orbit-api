using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Api.Controllers;
using Orbit.Infrastructure.Services;
using Stripe;

namespace Orbit.Infrastructure.Tests.Health;

public class StripeWebhookVersionHealthCheckTests
{
    private const string StagingWebhookUrl = "https://api-staging.useorbit.org/api/subscriptions/webhook";
    private const string StagingIncidentVersion = "2026-02-25.clover";

    private readonly WebhookEndpointService _endpoints = Substitute.For<WebhookEndpointService>();
    private readonly SteppingClock _clock = new();

    private StripeWebhookVersionHealthCheck CreateCheck() =>
        new(_endpoints, _clock, NullLogger<StripeWebhookVersionHealthCheck>.Instance);

    private void ListEndpoints(params WebhookEndpoint[] endpoints) =>
        _endpoints.ListAutoPagingAsync(Arg.Any<WebhookEndpointListOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => endpoints.ToAsyncEnumerable());

    private static WebhookEndpoint Endpoint(string? apiVersion, string status = "enabled", string url = StagingWebhookUrl) =>
        new() { Id = $"we_{Guid.NewGuid():N}", ApiVersion = apiVersion, Status = status, Url = url };

    private Task<HealthCheckResult> RunAsync(StripeWebhookVersionHealthCheck check) =>
        check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

    [Fact]
    public async Task EndpointAtLibraryVersion_IsHealthy()
    {
        ListEndpoints(Endpoint(StripeConfiguration.ApiVersion));

        var result = await RunAsync(CreateCheck());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["libraryApiVersion"].Should().Be(StripeConfiguration.ApiVersion);
        result.Data["endpointApiVersions"].Should().BeEquivalentTo(new[] { StripeConfiguration.ApiVersion });
    }

    [Fact]
    public async Task EndpointAtTheStagingIncidentVersion_IsUnhealthy()
    {
        ListEndpoints(Endpoint(StagingIncidentVersion));

        var result = await RunAsync(CreateCheck());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain(StagingIncidentVersion).And.Contain(StripeConfiguration.ApiVersion);
        result.Data["endpointApiVersions"].Should().BeEquivalentTo(new[] { StagingIncidentVersion });
    }

    [Fact]
    public async Task EndpointAtAnotherDateOfTheLibraryMajorVersion_IsHealthy_BecauseTheHandlerAcceptsIt()
    {
        var sameMajor = $"2099-12-31.{StripeConfiguration.MajorApiVersion}";
        ListEndpoints(Endpoint(sameMajor));

        var result = await RunAsync(CreateCheck());

        result.Status.Should().Be(HealthStatus.Healthy);
        var acceptedByHandler = () => EventUtility.ParseEvent(
            """{"id":"evt_probe","object":"event","api_version":""" + JsonSerializer.Serialize(sameMajor)
                + ""","type":"ping","data":{"object":{}}}""",
            throwOnApiVersionMismatch: true);
        acceptedByHandler.Should().NotThrow();
    }

    [Fact]
    public async Task OldAndNewEndpointsTogether_IsHealthy_SoAVersionChangeCanOverlap()
    {
        ListEndpoints(Endpoint(StagingIncidentVersion), Endpoint(StripeConfiguration.ApiVersion));

        var result = await RunAsync(CreateCheck());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["endpointApiVersions"].Should().BeEquivalentTo(new[] { StagingIncidentVersion, StripeConfiguration.ApiVersion });
    }

    [Theory]
    [InlineData("disabled", StagingWebhookUrl)]
    [InlineData("enabled", "https://api-staging.useorbit.org/api/other/webhook")]
    [InlineData("enabled", "not a url")]
    public async Task MatchingVersionOnADisabledOrUnrelatedEndpoint_DoesNotCount(string status, string url)
    {
        ListEndpoints(Endpoint(StripeConfiguration.ApiVersion, status, url));

        var result = await RunAsync(CreateCheck());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data["endpointApiVersions"].Should().BeEquivalentTo(Array.Empty<string>());
    }

    [Fact]
    public async Task EndpointUrlWithATrailingSlash_StillCounts()
    {
        ListEndpoints(Endpoint(StripeConfiguration.ApiVersion, url: StagingWebhookUrl + "/"));

        var result = await RunAsync(CreateCheck());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task EndpointOnTheAccountDefaultVersion_IsUnhealthy_BecauseItsVersionIsUnknown()
    {
        ListEndpoints(Endpoint(apiVersion: null));

        var result = await RunAsync(CreateCheck());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data["endpointApiVersions"].Should().BeEquivalentTo(new[] { StripeWebhookVersionHealthCheck.AccountDefaultVersion });
    }

    [Fact]
    public async Task StripeApiFailure_IsDegraded()
    {
        _endpoints.ListAutoPagingAsync(Arg.Any<WebhookEndpointListOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Throws(new StripeException("Invalid API Key provided"));

        var result = await RunAsync(CreateCheck());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["libraryApiVersion"].Should().Be(StripeConfiguration.ApiVersion);
    }

    [Fact]
    public async Task Result_IsReusedForOneMinute_ThenReadAgain()
    {
        ListEndpoints(Endpoint(StagingIncidentVersion));
        var check = CreateCheck();

        (await RunAsync(check)).Status.Should().Be(HealthStatus.Unhealthy);
        ListEndpoints(Endpoint(StripeConfiguration.ApiVersion));
        _clock.Advance(StripeWebhookVersionHealthCheck.ResultLifetime - TimeSpan.FromSeconds(1));
        (await RunAsync(check)).Status.Should().Be(HealthStatus.Unhealthy);
        _clock.Advance(TimeSpan.FromSeconds(1));
        (await RunAsync(check)).Status.Should().Be(HealthStatus.Healthy);

        _endpoints.Received(2).ListAutoPagingAsync(Arg.Any<WebhookEndpointListOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void WebhookPath_IsTheRouteOfTheWebhookAction()
    {
        var controllerRoute = typeof(SubscriptionController).GetCustomAttribute<RouteAttribute>()!.Template;
        var actionRoute = typeof(SubscriptionController).GetMethod(nameof(SubscriptionController.HandleWebhook))!
            .GetCustomAttribute<HttpPostAttribute>()!.Template;

        StripeWebhookVersionHealthCheck.WebhookPath.Should().Be($"/{controllerRoute}/{actionRoute}");
    }

    private sealed class SteppingClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
