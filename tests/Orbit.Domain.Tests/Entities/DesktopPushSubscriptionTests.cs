using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class DesktopPushSubscriptionTests
{
    [Theory]
    [InlineData("https://notify.windows.com/?token=channel", true)]
    [InlineData("https://wns2-by3p.notify.windows.com/?token=channel", true)]
    [InlineData("https://DM3P.NOTIFY.WINDOWS.COM/?token=channel", true)]
    [InlineData("https://notify.windows.com.attacker.org/", false)]
    [InlineData("https://evilnotify.windows.com/", false)]
    [InlineData("https://notify.windows.com@127.0.0.1/", false)]
    [InlineData("https://user@dm3p.notify.windows.com/", false)]
    [InlineData("https://dm3p.notify.windows.com:444/", false)]
    [InlineData("https://dm3p.notify.windows.com/#fragment", false)]
    [InlineData("http://dm3p.notify.windows.com/", false)]
    [InlineData("https://localhost/", false)]
    [InlineData("https://fcm.googleapis.com/", false)]
    [InlineData("not-a-uri", false)]
    public void WnsRegistration_EnforcesProviderPolicy(string endpoint, bool allowed)
    {
        WnsEndpointPolicy.IsAllowed(endpoint).Should().Be(allowed);
        PushSubscription.Create(Guid.NewGuid(), endpoint, "wns", "wns", deviceId: Guid.NewGuid())
            .IsSuccess.Should().Be(allowed);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("org.useorbit.desktop", true)]
    [InlineData("org.useorbit.desktop.staging", true)]
    [InlineData("org.attacker.app", false)]
    [InlineData("", false)]
    public void ApnsRegistration_RestrictsExplicitTopics(string? topic, bool allowed)
    {
        var result = PushSubscription.Create(Guid.NewGuid(), "token", "apns", "apns", topic);
        result.IsSuccess.Should().Be(allowed);
        if (allowed)
            result.Value.ApnsTopic.Should().Be(topic);
    }

    [Fact]
    public void WnsRegistration_RequiresStableNonemptyDeviceId()
    {
        foreach (var deviceId in new Guid?[] { null, Guid.Empty })
            PushSubscription.Create(Guid.NewGuid(), "https://dm3p.notify.windows.com/?token=channel", "wns", "wns", deviceId: deviceId)
                .ErrorCode.Should().Be(DomainErrors.PushDeviceIdInvalid.Code);
        PushSubscription.ClassifyTransport("wns").Should().Be(PushTransport.Wns);
    }

    [Fact]
    public void Registration_RejectsMetadataOnOtherTransports()
    {
        PushSubscription.Create(Guid.NewGuid(), "token", "fcm", "fcm", "org.useorbit.desktop")
            .ErrorCode.Should().Be(DomainErrors.PushApnsTopicInvalid.Code);
        PushSubscription.Create(Guid.NewGuid(), "token", "fcm", "fcm", deviceId: Guid.NewGuid())
            .ErrorCode.Should().Be(DomainErrors.PushDeviceIdInvalid.Code);
    }

    [Fact]
    public void Renewal_ReplacesChannelAndKeepsIdentity_RefusesUntrustedEndpoint()
    {
        var deviceId = Guid.NewGuid();
        var subscription = PushSubscription.Create(Guid.NewGuid(), "https://dm3p.notify.windows.com/?token=old", "wns", "wns", deviceId: deviceId).Value;
        var id = subscription.Id;
        subscription.RenewRegistration("https://dm3p.notify.windows.com/?token=new", "wns", null, deviceId).IsSuccess.Should().BeTrue();
        subscription.Endpoint.Should().EndWith("token=new");
        subscription.Id.Should().Be(id);
        subscription.RenewRegistration("https://attacker.org/", "wns", null, deviceId).IsFailure.Should().BeTrue();
        subscription.Endpoint.Should().EndWith("token=new");
    }
}
