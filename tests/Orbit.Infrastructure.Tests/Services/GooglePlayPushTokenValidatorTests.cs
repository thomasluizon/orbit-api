using FluentAssertions;
using Google.Apis.Auth;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class GooglePlayPushTokenValidatorTests
{
    private const string ServiceAccountEmail = "rtdn@orbit-test.iam.gserviceaccount.com";

    private static GooglePlaySettings Settings() => new()
    {
        RtdnAudience = "https://api.useorbit.test/play/rtdn",
        RtdnServiceAccountEmail = ServiceAccountEmail,
    };

    private static GooglePlayPushTokenValidator CreateValidator() =>
        new(Options.Create(Settings()));

    private static GoogleJsonWebSignature.Payload SenderPayload(
        string? issuer = "https://accounts.google.com",
        string email = ServiceAccountEmail,
        bool emailVerified = true) =>
        new() { Issuer = issuer, Email = email, EmailVerified = emailVerified };

    [Fact]
    public async Task IsValidAsync_MissingBearerPrefix_ReturnsFalse()
    {
        var result = await CreateValidator().IsValidAsync("not-a-bearer-token");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsValidAsync_EmptyHeader_ReturnsFalse()
    {
        var result = await CreateValidator().IsValidAsync(string.Empty);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsValidAsync_MalformedBearerToken_ReturnsFalse()
    {
        var result = await CreateValidator().IsValidAsync("Bearer not-a-real-jwt");

        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("https://accounts.google.com")]
    [InlineData("accounts.google.com")]
    public void IsAuthenticatedRtdnSender_AcceptedGoogleIssuer_ReturnsTrue(string issuer)
    {
        var result = GooglePlayPushTokenValidator.IsAuthenticatedRtdnSender(
            SenderPayload(issuer: issuer), Settings());

        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("accounts.google.com.evil.example.com")]
    [InlineData("https://sts.google.com")]
    [InlineData("")]
    [InlineData(null)]
    public void IsAuthenticatedRtdnSender_WrongOrMissingIssuer_ReturnsFalse(string? issuer)
    {
        var result = GooglePlayPushTokenValidator.IsAuthenticatedRtdnSender(
            SenderPayload(issuer: issuer), Settings());

        result.Should().BeFalse();
    }

    [Fact]
    public void IsAuthenticatedRtdnSender_UnverifiedEmail_ReturnsFalse()
    {
        var result = GooglePlayPushTokenValidator.IsAuthenticatedRtdnSender(
            SenderPayload(emailVerified: false), Settings());

        result.Should().BeFalse();
    }

    [Fact]
    public void IsAuthenticatedRtdnSender_WrongServiceAccountEmail_ReturnsFalse()
    {
        var result = GooglePlayPushTokenValidator.IsAuthenticatedRtdnSender(
            SenderPayload(email: "attacker@evil.iam.gserviceaccount.com"), Settings());

        result.Should().BeFalse();
    }

    [Fact]
    public void IsAuthenticatedRtdnSender_ServiceAccountEmailCaseInsensitive_ReturnsTrue()
    {
        var result = GooglePlayPushTokenValidator.IsAuthenticatedRtdnSender(
            SenderPayload(email: ServiceAccountEmail.ToUpperInvariant()), Settings());

        result.Should().BeTrue();
    }

    [Fact]
    public void IsAuthenticatedRtdnSender_ValidGoogleIssuerButWrongEmail_RejectsSpoofedSender()
    {
        var result = GooglePlayPushTokenValidator.IsAuthenticatedRtdnSender(
            SenderPayload(issuer: "https://accounts.google.com", email: "spoof@appspot.gserviceaccount.com"),
            Settings());

        result.Should().BeFalse();
    }
}
