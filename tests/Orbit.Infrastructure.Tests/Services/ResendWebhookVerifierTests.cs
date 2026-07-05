using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class ResendWebhookVerifierTests
{
    private static readonly byte[] KeyBytes = Encoding.UTF8.GetBytes("orbit-test-signing-secret-000001");
    private static readonly string Secret = "whsec_" + Convert.ToBase64String(KeyBytes);

    private const string Payload =
        """{"type":"contact.updated","data":{"email":"a@b.com","unsubscribed":true}}""";

    private static ResendWebhookVerifier BuildVerifier(string secret) =>
        new(Options.Create(new ResendSettings { ApiKey = "k", FromEmail = "f@f.com", WebhookSecret = secret }));

    private static (string Id, string Timestamp, string Signature) Sign(string payload)
    {
        const string id = "msg_test";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        using var hmac = new HMACSHA256(KeyBytes);
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{id}.{timestamp}.{payload}")));
        return (id, timestamp, $"v1,{signature}");
    }

    [Fact]
    public void Verify_ValidSignature_ReturnsVerified()
    {
        var (id, timestamp, signature) = Sign(Payload);

        var result = BuildVerifier(Secret).Verify(Payload, id, timestamp, signature);

        result.Should().Be(ResendWebhookVerification.Verified);
    }

    [Fact]
    public void Verify_TamperedPayload_ReturnsInvalidSignature()
    {
        var (id, timestamp, signature) = Sign(Payload);

        var result = BuildVerifier(Secret).Verify(Payload + "tampered", id, timestamp, signature);

        result.Should().Be(ResendWebhookVerification.InvalidSignature);
    }

    [Fact]
    public void Verify_EmptySecret_ReturnsSecretNotConfigured()
    {
        var (id, timestamp, signature) = Sign(Payload);

        var result = BuildVerifier("").Verify(Payload, id, timestamp, signature);

        result.Should().Be(ResendWebhookVerification.SecretNotConfigured);
    }
}
