using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Api.Extensions;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Extensions;

/// <summary>
/// The PostHog kill switch: the project key decides which capture implementation is bound, so removing
/// the Render env var reverts the API to a build that emits no analytics traffic at all.
/// </summary>
public class ProductAnalyticsRegistrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddOrbitProductAnalytics_MissingOrBlankApiKey_BindsNoOp(string? apiKey)
    {
        var builder = BuildWith(new Dictionary<string, string?> { ["PostHog:ApiKey"] = apiKey });

        builder.AddOrbitProductAnalytics();

        Resolve(builder).Should().BeOfType<NoOpProductAnalytics>();
    }

    [Fact]
    public void AddOrbitProductAnalytics_ApiKeyPresent_BindsPostHogBackedImplementation()
    {
        var builder = BuildWith(new Dictionary<string, string?>
        {
            ["PostHog:ApiKey"] = "phc_test_project_key",
            ["PostHog:HostUrl"] = "https://us.i.posthog.com"
        });

        builder.AddOrbitProductAnalytics();

        Resolve(builder).Should().BeOfType<PostHogProductAnalytics>();
    }

    [Fact]
    public void NoOpProductAnalytics_Capture_DoesNothingAndDoesNotThrow()
    {
        var act = () => new NoOpProductAnalytics()
            .CaptureUserEvent(Guid.NewGuid(), "subscription_started", "Pro");

        act.Should().NotThrow();
    }

    private static IProductAnalytics Resolve(WebApplicationBuilder builder) =>
        builder.Services.BuildServiceProvider().GetRequiredService<IProductAnalytics>();

    private static WebApplicationBuilder BuildWith(Dictionary<string, string?> values)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(values);
        return builder;
    }
}
