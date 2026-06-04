using FluentAssertions;
using Orbit.Application.Common;

namespace Orbit.Application.Tests.Common;

public class StripeSettingsTests
{
    private static StripeSettings AllPriceIdsSet() => new()
    {
        MonthlyPriceIdUsd = "price_monthly_usd",
        YearlyPriceIdUsd = "price_yearly_usd",
        MonthlyPriceIdBrl = "price_monthly_brl",
        YearlyPriceIdBrl = "price_yearly_brl"
    };

    [Fact]
    public void ValidatePriceIds_AllSet_DoesNotThrow()
    {
        var act = () => AllPriceIdsSet().ValidatePriceIds();

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(nameof(StripeSettings.MonthlyPriceIdUsd))]
    [InlineData(nameof(StripeSettings.YearlyPriceIdUsd))]
    [InlineData(nameof(StripeSettings.MonthlyPriceIdBrl))]
    [InlineData(nameof(StripeSettings.YearlyPriceIdBrl))]
    public void ValidatePriceIds_OneBlank_ThrowsNamingMissingKey(string blankProperty)
    {
        var settings = AllPriceIdsSet();
        typeof(StripeSettings).GetProperty(blankProperty)!.SetValue(settings, "  ");

        var act = () => settings.ValidatePriceIds();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{StripeSettings.SectionName}:{blankProperty}*");
    }
}
