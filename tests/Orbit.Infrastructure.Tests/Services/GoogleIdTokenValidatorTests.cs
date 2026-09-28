using FluentAssertions;
using Google.Apis.Auth;
using Orbit.Application.Common;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class GoogleIdTokenValidatorTests
{
    private const long Now = 2_000_000_000;

    private static GoogleJsonWebSignature.Payload Payload() => new()
    {
        Issuer = "https://accounts.google.com",
        Audience = "orbit-client",
        ExpirationTimeSeconds = Now + 60,
        Email = "google@example.com",
        Name = "Google User",
        EmailVerified = true
    };

    [Theory]
    [InlineData("https://accounts.google.com")]
    [InlineData("accounts.google.com")]
    public void ValidClaims_AreAccepted(string issuer)
    {
        var payload = Payload();
        payload.Issuer = issuer;

        var result = GoogleIdTokenValidator.ValidateClaims(payload, "orbit-client", Now);

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("Google User");
    }

    [Fact]
    public void WrongAudience_IsRefused()
    {
        var payload = Payload();
        payload.Audience = "other-client";

        GoogleIdTokenValidator.ValidateClaims(payload, "orbit-client", Now).ErrorCode
            .Should().Be(ErrorCodes.InvalidGoogleToken);
    }

    [Fact]
    public void ExpiredToken_IsRefused()
    {
        var payload = Payload();
        payload.ExpirationTimeSeconds = Now;

        GoogleIdTokenValidator.ValidateClaims(payload, "orbit-client", Now).ErrorCode
            .Should().Be(ErrorCodes.InvalidGoogleToken);
    }

    [Fact]
    public void UnverifiedEmail_IsRefused()
    {
        var payload = Payload();
        payload.EmailVerified = false;

        GoogleIdTokenValidator.ValidateClaims(payload, "orbit-client", Now).ErrorCode
            .Should().Be(ErrorCodes.InvalidGoogleToken);
    }

    [Fact]
    public void MissingEmail_HasSpecificError()
    {
        var payload = Payload();
        payload.Email = null;

        GoogleIdTokenValidator.ValidateClaims(payload, "orbit-client", Now).ErrorCode
            .Should().Be(ErrorCodes.GoogleEmailUnavailable);
    }
}
