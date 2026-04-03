using System.Text.Json;
using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the push subscription routing logic used by PushNotificationService.
/// FCM subscriptions are identified by P256dh == "fcm", everything else is web push.
/// The actual send logic requires Firebase/WebPush infrastructure, tested at integration level.
/// </summary>
public class PushNotificationServiceTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();

    [Fact]
    public void PushSubscription_FcmSubscription_IdentifiedByP256dhFcm()
    {
        // Arrange
        var sub = PushSubscription.Create(ValidUserId, "fcm-token-123", "fcm", "auth-key").Value;

        // Assert
        sub.P256dh.Should().Be("fcm");
        sub.Endpoint.Should().Be("fcm-token-123");
    }

    [Fact]
    public void PushSubscription_WebPushSubscription_HasRealP256dh()
    {
        // Arrange
        var sub = PushSubscription.Create(
            ValidUserId,
            "https://fcm.googleapis.com/fcm/send/abc123",
            "BNcRdreA...real-key",
            "auth-secret").Value;

        // Assert
        sub.P256dh.Should().NotBe("fcm");
    }

    [Fact]
    public void PushSubscription_FcmRoutingDecision_CorrectlySplits()
    {
        // Arrange -- simulate the routing logic from PushNotificationService
        var subs = new List<PushSubscription>
        {
            PushSubscription.Create(ValidUserId, "fcm-token-1", "fcm", "auth1").Value,
            PushSubscription.Create(ValidUserId, "fcm-token-2", "fcm", "auth2").Value,
            PushSubscription.Create(ValidUserId, "https://push.example.com/sub1", "p256dh-key-1", "auth3").Value,
            PushSubscription.Create(ValidUserId, "https://push.example.com/sub2", "p256dh-key-2", "auth4").Value,
        };

        // Act -- replicate the routing logic
        var fcmSubs = subs.Where(s => s.P256dh == "fcm").ToList();
        var webPushSubs = subs.Where(s => s.P256dh != "fcm").ToList();

        // Assert
        fcmSubs.Should().HaveCount(2);
        webPushSubs.Should().HaveCount(2);
        fcmSubs.Should().AllSatisfy(s => s.P256dh.Should().Be("fcm"));
        webPushSubs.Should().AllSatisfy(s => s.P256dh.Should().NotBe("fcm"));
    }

    [Fact]
    public void PushSubscription_EmptyList_NoRouting()
    {
        // Arrange
        var subs = new List<PushSubscription>();

        // Act
        var fcmSubs = subs.Where(s => s.P256dh == "fcm").ToList();
        var webPushSubs = subs.Where(s => s.P256dh != "fcm").ToList();

        // Assert
        fcmSubs.Should().BeEmpty();
        webPushSubs.Should().BeEmpty();
    }

    [Fact]
    public void PushSubscription_AllFcm_NoWebPush()
    {
        // Arrange
        var subs = new List<PushSubscription>
        {
            PushSubscription.Create(ValidUserId, "token-1", "fcm", "auth1").Value,
            PushSubscription.Create(ValidUserId, "token-2", "fcm", "auth2").Value,
        };

        // Act
        var fcmSubs = subs.Where(s => s.P256dh == "fcm").ToList();
        var webPushSubs = subs.Where(s => s.P256dh != "fcm").ToList();

        // Assert
        fcmSubs.Should().HaveCount(2);
        webPushSubs.Should().BeEmpty();
    }

    [Fact]
    public void PushSubscription_AllWebPush_NoFcm()
    {
        // Arrange
        var subs = new List<PushSubscription>
        {
            PushSubscription.Create(ValidUserId, "https://push.example.com/1", "real-key-1", "auth1").Value,
            PushSubscription.Create(ValidUserId, "https://push.example.com/2", "real-key-2", "auth2").Value,
        };

        // Act
        var fcmSubs = subs.Where(s => s.P256dh == "fcm").ToList();
        var webPushSubs = subs.Where(s => s.P256dh != "fcm").ToList();

        // Assert
        fcmSubs.Should().BeEmpty();
        webPushSubs.Should().HaveCount(2);
    }

    // ── PushSubscription factory validation ──────────────────────────

    [Fact]
    public void Create_EmptyUserId_ReturnsFailure()
    {
        var result = PushSubscription.Create(Guid.Empty, "endpoint", "key", "auth");
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_EmptyEndpoint_ReturnsFailure()
    {
        var result = PushSubscription.Create(ValidUserId, "", "key", "auth");
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_EmptyP256dh_ReturnsFailure()
    {
        var result = PushSubscription.Create(ValidUserId, "endpoint", "", "auth");
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_EmptyAuth_ReturnsFailure()
    {
        var result = PushSubscription.Create(ValidUserId, "endpoint", "key", "");
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_ValidInputs_ReturnsSuccess()
    {
        var result = PushSubscription.Create(ValidUserId, "https://push.example.com", "p256dh-key", "auth-secret");
        result.IsSuccess.Should().BeTrue();
        result.Value.UserId.Should().Be(ValidUserId);
    }

    // ── Token preview logic (replicates SendFcmToSubscription preview logic) ──

    [Fact]
    public void TokenPreview_ShortEndpoint_TruncatesCorrectly()
    {
        var endpoint = "short";
        var preview = endpoint[..Math.Min(20, endpoint.Length)] + "...";

        preview.Should().Be("short...");
    }

    [Fact]
    public void TokenPreview_LongEndpoint_TruncatesTo20Chars()
    {
        var endpoint = "https://push.example.com/sub/abcdefghijklmnopqrst";
        var preview = endpoint[..Math.Min(20, endpoint.Length)] + "...";

        preview.Should().Be("https://push.example...");
        preview.Should().HaveLength(23); // 20 + 3 for "..."
    }

    [Fact]
    public void TokenPreview_ExactlyTwentyChars_NoExtraTruncation()
    {
        var endpoint = "12345678901234567890";
        var preview = endpoint[..Math.Min(20, endpoint.Length)] + "...";

        preview.Should().Be("12345678901234567890...");
    }

    // ── Web push payload serialization ──

    [Fact]
    public void WebPushPayload_SerializesCorrectly_WithUrl()
    {
        var title = "Test Title";
        var body = "Test Body";
        var url = "/habits";

        var payload = JsonSerializer.Serialize(new { title, body, url });

        payload.Should().Contain("\"title\":\"Test Title\"");
        payload.Should().Contain("\"body\":\"Test Body\"");
        payload.Should().Contain("\"url\":\"/habits\"");
    }

    [Fact]
    public void WebPushPayload_SerializesCorrectly_WithNullUrl()
    {
        var title = "Alert";
        var body = "Something happened";
        string? url = null;

        var payload = JsonSerializer.Serialize(new { title, body, url });

        payload.Should().Contain("\"url\":null");
    }

    [Fact]
    public void WebPushPayload_SpecialCharacters_EscapedInJson()
    {
        var title = "He said \"hello\"";
        var body = "Line1\nLine2";
        var url = "/";

        var payload = JsonSerializer.Serialize(new { title, body, url });
        var parsed = JsonDocument.Parse(payload);

        parsed.RootElement.GetProperty("title").GetString().Should().Be("He said \"hello\"");
        parsed.RootElement.GetProperty("body").GetString().Should().Be("Line1\nLine2");
    }

    // ── Subscription multi-user routing ──

    [Fact]
    public void MultiUserRouting_GroupsByUserId()
    {
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        var subs = new List<PushSubscription>
        {
            PushSubscription.Create(user1, "token-1", "fcm", "auth1").Value,
            PushSubscription.Create(user1, "https://push.com/1", "key1", "auth2").Value,
            PushSubscription.Create(user2, "token-2", "fcm", "auth3").Value,
        };

        var user1Subs = subs.Where(s => s.UserId == user1).ToList();
        var user2Subs = subs.Where(s => s.UserId == user2).ToList();

        user1Subs.Should().HaveCount(2);
        user2Subs.Should().HaveCount(1);
    }

    [Fact]
    public void StaleSubscriptionTracking_AccumulatesAcrossTypes()
    {
        // Simulate the stale subscription accumulation logic
        var staleSubscriptions = new List<PushSubscription>();

        var fcmSub = PushSubscription.Create(ValidUserId, "stale-token", "fcm", "auth1").Value;
        var webSub = PushSubscription.Create(ValidUserId, "https://gone.com/sub", "key1", "auth2").Value;

        // Simulate FCM marking stale
        staleSubscriptions.Add(fcmSub);
        // Simulate WebPush marking stale
        staleSubscriptions.Add(webSub);

        staleSubscriptions.Should().HaveCount(2);
        staleSubscriptions.Should().Contain(fcmSub);
        staleSubscriptions.Should().Contain(webSub);
    }
}
