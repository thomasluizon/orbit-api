using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class PushSubscriptionTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();

    [Fact]
    public void Create_Valid_Success()
    {
        var result = PushSubscription.Create(
            ValidUserId,
            "https://push.example.com/sub/123",
            "BNcR..p256dh-key",
            "auth-secret");

        result.IsSuccess.Should().BeTrue();
        result.Value.UserId.Should().Be(ValidUserId);
        result.Value.Endpoint.Should().Be("https://push.example.com/sub/123");
        result.Value.P256dh.Should().Be("BNcR..p256dh-key");
        result.Value.Auth.Should().Be("auth-secret");
    }

    [Fact]
    public void Create_EmptyUserId_Failure()
    {
        var result = PushSubscription.Create(
            Guid.Empty,
            "https://push.example.com/sub/123",
            "p256dh-key",
            "auth-secret");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User ID is required");
    }

    [Fact]
    public void Create_EmptyEndpoint_Failure()
    {
        var result = PushSubscription.Create(
            ValidUserId,
            "",
            "p256dh-key",
            "auth-secret");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Endpoint is required");
    }

    [Fact]
    public void Create_EmptyP256dh_Failure()
    {
        var result = PushSubscription.Create(
            ValidUserId,
            "https://push.example.com/sub/123",
            "",
            "auth-secret");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("P256dh key is required");
    }

    [Fact]
    public void Create_EmptyAuth_Failure()
    {
        var result = PushSubscription.Create(
            ValidUserId,
            "https://push.example.com/sub/123",
            "p256dh-key",
            "");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Auth key is required");
    }
}
