using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Infrastructure.Services;
using PostHog;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Pins the PostHog wire shape: the Orbit user id is the distinct id, <c>plan</c> travels under
/// <c>$set</c>, and the retired yearly entitlement property travels under <c>$unset</c>.
/// </summary>
public class PostHogProductAnalyticsTests
{
    private readonly IPostHogClient _postHogClient = Substitute.For<IPostHogClient>();
    private readonly PostHogProductAnalytics _analytics;

    public PostHogProductAnalyticsTests() =>
        _analytics = new PostHogProductAnalytics(
            _postHogClient, Substitute.For<ILogger<PostHogProductAnalytics>>());

    [Fact]
    public void CaptureUserEvent_SendsUserIdAsDistinctIdWithPersonProperties()
    {
        var userId = Guid.NewGuid();

        _analytics.CaptureUserEvent(userId, "subscription_started", "Pro");

        var arguments = _postHogClient.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IPostHogClient.Capture))
            .GetArguments();

        arguments[0].Should().Be(userId.ToString());
        arguments[1].Should().Be("subscription_started");

        var properties = arguments[2].Should().BeAssignableTo<Dictionary<string, object>>().Subject;
        var personProperties = properties["$set"].Should().BeAssignableTo<Dictionary<string, object>>().Subject;
        personProperties["plan"].Should().Be("Pro");
        personProperties.Should().HaveCount(1);
        properties["$unset"].Should().BeEquivalentTo(new[] { "isYearlyPro" });
    }

    [Fact]
    public void CaptureUserEvent_DoesNotStampAnExplicitTimestamp()
    {
        _analytics.CaptureUserEvent(Guid.NewGuid(), "signup_completed", "Free");

        var arguments = _postHogClient.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IPostHogClient.Capture))
            .GetArguments();

        arguments[^1].Should().BeNull();
    }
}
