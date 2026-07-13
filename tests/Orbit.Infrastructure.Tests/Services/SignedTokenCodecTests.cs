using System.Text;
using FluentAssertions;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class SignedTokenCodecTests
{
    private const string SigningKey = "signed-token-codec-signing-key-32-bytes-long";

    [Fact]
    public void Encode_ThenDecode_RoundTripsPayloadBytes()
    {
        var payload = Encoding.UTF8.GetBytes("payload|value|123");

        var token = SignedTokenCodec.Encode(payload, SigningKey);
        var decoded = SignedTokenCodec.TryDecode(token, SigningKey, out var payloadBytes);

        decoded.Should().BeTrue();
        payloadBytes.Should().Equal(payload);
    }

    [Fact]
    public void TryDecode_WrongSigningKey_Fails()
    {
        var token = SignedTokenCodec.Encode(Encoding.UTF8.GetBytes("payload"), SigningKey);

        var decoded = SignedTokenCodec.TryDecode(token, "a-different-signing-key-value-here-32", out var payloadBytes);

        decoded.Should().BeFalse();
        payloadBytes.Should().BeEmpty();
    }

    [Fact]
    public void TryDecode_TamperedSignature_Fails()
    {
        var token = SignedTokenCodec.Encode(Encoding.UTF8.GetBytes("payload"), SigningKey);
        var tampered = token[..^2] + (token.EndsWith('a') ? "bb" : "aa");

        SignedTokenCodec.TryDecode(tampered, SigningKey, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-separator")]
    [InlineData("only.")]
    [InlineData(".only")]
    [InlineData("!!!.@@@")]
    [InlineData("aaaaa.bbbb")]
    public void TryDecode_MalformedToken_Fails(string token)
    {
        var decoded = SignedTokenCodec.TryDecode(token, SigningKey, out var payloadBytes);

        decoded.Should().BeFalse();
        payloadBytes.Should().BeEmpty();
    }
}
