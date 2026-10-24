using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class DesktopHandoffTests
{
    private const string Redirect = "org.useorbit.desktop://auth/callback";
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    [Fact]
    public void Create_BindsAccountAndExactInputs_ForSixtySeconds()
    {
        var userId = Guid.NewGuid();
        var issuedAtUtc = DateTime.UtcNow;
        var result = DesktopHandoff.Create(userId, new string('A', 64), " State ", Challenge, "S256", Redirect, Redirect, issuedAtUtc);

        result.IsSuccess.Should().BeTrue();
        result.Value.UserId.Should().Be(userId);
        result.Value.State.Should().Be(" State ");
        result.Value.CodeChallenge.Should().Be(Challenge);
        result.Value.RedirectUri.Should().Be(Redirect);
        result.Value.CodeHash.Should().Be(new string('A', 64));
        result.Value.ExpiresAtUtc.Should().Be(issuedAtUtc.AddSeconds(60));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("hash")]
    [InlineData("state")]
    [InlineData("state-length")]
    [InlineData("challenge")]
    [InlineData("challenge-characters")]
    [InlineData("method")]
    [InlineData("redirect")]
    [InlineData("redirect-case")]
    [InlineData("redirect-query")]
    [InlineData("allowlist")]
    [InlineData("timestamp")]
    public void Create_InvalidBinding_ReturnsGenericFailure(string invalid)
    {
        var result = DesktopHandoff.Create(
            invalid == "user" ? Guid.Empty : Guid.NewGuid(),
            invalid == "hash" ? "raw-code" : new string('A', 64),
            invalid == "state" ? " " : invalid == "state-length" ? new string('s', 513) : "state",
            invalid == "challenge" ? "short" : invalid == "challenge-characters" ? new string('+', 43) : Challenge,
            invalid == "method" ? "plain" : "S256",
            invalid switch
            {
                "redirect" => "org.useorbit.desktop.staging://auth/callback",
                "redirect-case" => "org.useorbit.desktop://auth/Callback",
                "redirect-query" => Redirect + "?extra=value",
                _ => Redirect
            },
            invalid == "allowlist" ? "" : Redirect,
            invalid == "timestamp" ? DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified) : DateTime.UtcNow);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(DesktopHandoff.InvalidHandoff);
    }

    [Theory]
    [InlineData(42, false)]
    [InlineData(43, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void Verifier_EnforcesPkceLength(int length, bool valid) =>
        DesktopHandoff.IsValidVerifier(new string('a', length)).Should().Be(valid);

    [Theory]
    [InlineData(".", true)]
    [InlineData("~", true)]
    [InlineData("_", true)]
    [InlineData("-", true)]
    [InlineData("+", false)]
    [InlineData("=", false)]
    [InlineData("é", false)]
    public void Verifier_EnforcesUnreservedAscii(string suffix, bool valid) =>
        DesktopHandoff.IsValidVerifier(new string('a', 42) + suffix).Should().Be(valid);
}
