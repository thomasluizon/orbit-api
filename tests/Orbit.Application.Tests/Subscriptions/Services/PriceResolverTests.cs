using FluentAssertions;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Application.Subscriptions.Services;

namespace Orbit.Application.Tests.Subscriptions.Services;

public class PriceResolverTests
{
    private readonly PriceResolver _resolver = new(Options.Create(new StripeSettings
    {
        MonthlyPriceIdUsd = "price_monthly_usd",
        YearlyPriceIdUsd = "price_yearly_usd",
        MonthlyPriceIdBrl = "price_monthly_brl",
        YearlyPriceIdBrl = "price_yearly_brl"
    }));

    [Theory]
    [InlineData("monthly", true, "price_monthly_brl")]
    [InlineData("monthly", false, "price_monthly_usd")]
    [InlineData("yearly", true, "price_yearly_brl")]
    [InlineData("yearly", false, "price_yearly_usd")]
    public void Resolve_KnownIntervalAndAudience_ReturnsMatchingPriceId(string interval, bool isBrazil, string expected)
    {
        _resolver.Resolve(interval, isBrazil).Should().Be(expected);
    }

    [Fact]
    public void Resolve_UnknownInterval_Throws()
    {
        var act = () => _resolver.Resolve("weekly", false);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
