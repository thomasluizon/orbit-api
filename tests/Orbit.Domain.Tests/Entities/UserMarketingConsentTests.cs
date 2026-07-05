using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class UserMarketingConsentTests
{
    private static User CreateUser() => User.Create("Thomas", "thomas@example.com").Value;

    [Fact]
    public void NewUser_HasNullConsent()
    {
        var user = CreateUser();

        user.MarketingEmailConsent.Should().BeNull();
        user.MarketingConsentUpdatedAtUtc.Should().BeNull();
    }

    [Fact]
    public void SetMarketingConsent_OptIn_SetsValueAndTimestamp()
    {
        var user = CreateUser();

        user.SetMarketingConsent(true);

        user.MarketingEmailConsent.Should().BeTrue();
        user.MarketingConsentUpdatedAtUtc.Should().NotBeNull();
        user.MarketingConsentUpdatedAtUtc!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void SetMarketingConsent_OptOut_SetsFalseAndTimestamp()
    {
        var user = CreateUser();

        user.SetMarketingConsent(false);

        user.MarketingEmailConsent.Should().BeFalse();
        user.MarketingConsentUpdatedAtUtc.Should().NotBeNull();
    }
}
