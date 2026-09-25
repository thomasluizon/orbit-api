using FluentAssertions;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class MarketingUnsubscribeTokenServiceTests
{
    private const string SigningKey = "marketing-unsubscribe-signing-key-at-least-32-bytes";

    private static MarketingUnsubscribeTokenService Build(string signingKey = SigningKey) =>
        new(Options.Create(new MarketingSettings { UnsubscribeSigningKey = signingKey }));

    [Fact]
    public void CreateToken_ThenValidate_RoundTripsUserId()
    {
        var sut = Build();
        var userId = Guid.NewGuid();

        var token = sut.CreateToken(userId);
        var valid = sut.TryValidateToken(token, out var parsedUserId);

        valid.Should().BeTrue();
        parsedUserId.Should().Be(userId);
    }

    [Fact]
    public void TryValidateToken_TamperedToken_Fails()
    {
        var sut = Build();

        for (var attempt = 0; attempt < 500; attempt++)
        {
            var token = sut.CreateToken(Guid.NewGuid());
            var originalSignature = DecodeSignature(token);
            var tamperedSignature = (byte[])originalSignature.Clone();
            tamperedSignature[0] ^= 1;
            var encodedSignature = Convert.ToBase64String(tamperedSignature).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var tampered = token[..(token.IndexOf('.') + 1)] + encodedSignature;

            DecodeSignature(tampered).SequenceEqual(originalSignature).Should().BeFalse();

            var valid = sut.TryValidateToken(tampered, out var parsedUserId);

            valid.Should().BeFalse();
            parsedUserId.Should().Be(Guid.Empty);
        }
    }

    [Fact]
    public void TryValidateToken_TokenSignedWithDifferentKey_Fails()
    {
        var foreignToken = Build("a-completely-different-signing-key-value-here-32b").CreateToken(Guid.NewGuid());

        var valid = Build().TryValidateToken(foreignToken, out _);

        valid.Should().BeFalse();
    }

    [Fact]
    public void TryValidateToken_EmptyToken_Fails()
    {
        Build().TryValidateToken("", out var parsedUserId).Should().BeFalse();
        parsedUserId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void CreateToken_NoSigningKeyConfigured_Throws()
    {
        var act = () => Build(signingKey: "").CreateToken(Guid.NewGuid());

        act.Should().Throw<InvalidOperationException>();
    }

    private static byte[] DecodeSignature(string token)
    {
        var signature = token[(token.IndexOf('.') + 1)..];
        return Convert.FromBase64String(signature.Replace('-', '+').Replace('_', '/') + "=");
    }
}
