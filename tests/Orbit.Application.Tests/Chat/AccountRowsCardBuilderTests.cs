using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Application.Referrals.Queries;

namespace Orbit.Application.Tests.Chat;

public class AccountRowsCardBuilderTests
{
    [Fact]
    public void Referral_UsesDashboardCodeAndStats()
    {
        var stats = new ReferralStatsResponse("other", "https://example.test/other", 3, 2, 10, "discount", 20);
        var dashboard = new ReferralDashboardResponse("CODE123", "https://example.test/r/CODE123", stats);

        var card = AccountRowsCardBuilder.Referral(dashboard);

        card.ReferralCode.Should().Be(dashboard.Code);
        card.ReferralLink.Should().Be(dashboard.Link);
        card.Rows.Single(row => row.Key == "successfulReferrals").Value.Should().Be("3");
    }
}
