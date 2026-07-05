using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class MarketingUnsubscribeTokenServiceTests
{
    private readonly IDataProtectionProvider _provider = new EphemeralDataProtectionProvider();
    private readonly MarketingUnsubscribeTokenService _sut;

    public MarketingUnsubscribeTokenServiceTests()
    {
        _sut = new MarketingUnsubscribeTokenService(_provider);
    }

    [Fact]
    public void CreateToken_ThenValidate_RoundTripsUserId()
    {
        var userId = Guid.NewGuid();

        var token = _sut.CreateToken(userId);
        var valid = _sut.TryValidateToken(token, out var parsedUserId);

        valid.Should().BeTrue();
        parsedUserId.Should().Be(userId);
    }

    [Fact]
    public void TryValidateToken_TamperedToken_Fails()
    {
        var token = _sut.CreateToken(Guid.NewGuid());
        var tampered = token[..^2] + (token.EndsWith('a') ? "bb" : "aa");

        var valid = _sut.TryValidateToken(tampered, out var parsedUserId);

        valid.Should().BeFalse();
        parsedUserId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void TryValidateToken_TokenFromDifferentPurpose_Fails()
    {
        var foreignProtector = _provider.CreateProtector("some-other-purpose");
        var foreignToken = foreignProtector.Protect($"{Guid.NewGuid():N}|0");

        var valid = _sut.TryValidateToken(foreignToken, out _);

        valid.Should().BeFalse();
    }

    [Fact]
    public void TryValidateToken_EmptyToken_Fails()
    {
        _sut.TryValidateToken("", out var parsedUserId).Should().BeFalse();
        parsedUserId.Should().Be(Guid.Empty);
    }
}
