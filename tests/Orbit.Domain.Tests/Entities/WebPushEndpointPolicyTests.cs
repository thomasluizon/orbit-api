using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class WebPushEndpointPolicyTests
{
    /// <summary>
    /// Sources: https://web.dev/articles/codelab-notifications-push-server,
    /// https://mozilla-services.github.io/autopush-rs/http.html,
    /// https://github.com/andreinwald/webpush-ios-example#subscription-and-saving-token, and
    /// WNS: https://learn.microsoft.com/en-us/windows/apps/develop/notifications/push-notifications/wns-overview; Edge: https://learn.microsoft.com/en-us/answers/questions/5593523/why-notifications-fail-in-azure-notification-hub-a.
    /// </summary>
    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/cpqAgzGzkzQ:APA9...")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v1/...")]
    [InlineData("https://web.push.apple.com/QGuQyavXutnMH...")]
    [InlineData("https://cloud.notify.windows.com/?token=AQE%bU%2fSjZOCvRjjpILow%3d%3d")]
    [InlineData("https://wns2-sg2p.notify.windows.com/w/?token=...")]
    [InlineData("https://FCM.GOOGLEAPIS.COM:443/opaque?token=abc")]
    [InlineData("https://another.push.apple.com/opaque")]
    [InlineData("https://another.notify.windows.com/opaque")]
    public void SupportedEndpoint_PolicyAndEntityAccept(string endpoint)
    {
        WebPushEndpointPolicy.IsAllowed(endpoint).Should().BeTrue();
        PushSubscription.Create(Guid.NewGuid(), endpoint, "p256dh", "auth").IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("https://example.com/push")]
    [InlineData("https://localhost/push")]
    [InlineData("https://127.0.0.1/push")]
    [InlineData("https://[::1]/push")]
    [InlineData("https://169.254.169.254/push")]
    [InlineData("http://fcm.googleapis.com/push")]
    [InlineData("https://fcm.googleapis.com:8443/push")]
    [InlineData("https://user:password@fcm.googleapis.com/push")]
    [InlineData("https://fcm.googleapis.com/push#fragment")]
    [InlineData("https://fcm.googleapis.com.example.com/push")]
    [InlineData("https://example.com/fcm.googleapis.com/push")]
    [InlineData("https://fcm.googleapis.com@example.com/push")]
    [InlineData("https://example.com/?host=fcm.googleapis.com")]
    [InlineData("https://evilfcm.googleapis.com/push")]
    [InlineData("https://fcm.googleapis.com./push")]
    [InlineData("https://sub.fcm.googleapis.com/push")]
    [InlineData("https://updates.push.services.mozilla.com.example.com/push")]
    [InlineData("https://evilupdates.push.services.mozilla.com/push")]
    [InlineData("https://web.push.apple.com.example.com/push")]
    [InlineData("https://evilpush.apple.com/push")]
    [InlineData("https://push.apple.com/push")]
    [InlineData("https://cloud.notify.windows.com.example.com/push")]
    [InlineData("https://evilnotify.windows.com/push")]
    [InlineData("https://notify.windows.com/push")]
    [InlineData("/relative")]
    [InlineData("not a URI")]
    public void UnsupportedEndpoint_PolicyAndEntityReject(string endpoint)
    {
        WebPushEndpointPolicy.IsAllowed(endpoint).Should().BeFalse();
        var result = PushSubscription.Create(Guid.NewGuid(), endpoint, "p256dh", "auth");
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DomainErrors.PushEndpointInvalid.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingEndpoint_PolicyRejects(string? endpoint)
    {
        WebPushEndpointPolicy.IsAllowed(endpoint).Should().BeFalse();
    }

    [Theory]
    [InlineData("native-device-token")]
    [InlineData("https://example.com/native-token")]
    public void NativeToken_DoesNotUseWebPushPolicy(string token)
    {
        PushSubscription.Create(Guid.NewGuid(), token, PushSubscription.FcmSentinel, "auth")
            .IsSuccess.Should().BeTrue();
    }
}
