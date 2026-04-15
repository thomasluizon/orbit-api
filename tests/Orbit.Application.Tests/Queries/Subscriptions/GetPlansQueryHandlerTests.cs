using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Common;
using Orbit.Application.Subscriptions.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.Subscriptions;

public class GetPlansQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGeoLocationService _geoLocationService = Substitute.For<IGeoLocationService>();
    private readonly IOptions<StripeSettings> _stripeSettings;
    private readonly IBillingService _billingService = Substitute.For<IBillingService>();
    private readonly ILogger<GetPlansQueryHandler> _logger = Substitute.For<ILogger<GetPlansQueryHandler>>();
    private readonly GetPlansQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetPlansQueryHandlerTests()
    {
        _stripeSettings = Options.Create(new StripeSettings
        {
            MonthlyPriceIdUsd = "price_monthly_usd",
            YearlyPriceIdUsd = "price_yearly_usd",
            MonthlyPriceIdBrl = "price_monthly_brl",
            YearlyPriceIdBrl = "price_yearly_brl"
        });
        _handler = new GetPlansQueryHandler(_userRepo, _geoLocationService, _stripeSettings, _billingService, _logger);
    }

    private static User CreateTestUser()
    {
        return User.Create("Test User", "test@example.com").Value;
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var query = new GetPlansQuery(UserId, null, null);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
        result.ErrorCode.Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_BillingProviderError_ReturnsFailure()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _geoLocationService.GetCountryCodeAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns("US");

        _billingService.GetPriceUnitAmountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new BillingProviderException("Stripe error"));

        var query = new GetPlansQuery(UserId, null, "1.2.3.4");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Payment service temporarily unavailable");
    }

    [Fact]
    public async Task Handle_BrazilianUser_UsesBrlPriceIds()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _geoLocationService.GetCountryCodeAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns("BR");

        _billingService.GetPriceUnitAmountAsync("price_monthly_brl", Arg.Any<CancellationToken>()).Returns(1990);
        _billingService.GetPriceUnitAmountAsync("price_yearly_brl", Arg.Any<CancellationToken>()).Returns(19900);

        var query = new GetPlansQuery(UserId, null, "200.100.50.1");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Currency.Should().Be("brl");
        result.Value.Monthly.Currency.Should().Be("brl");
        result.Value.Monthly.UnitAmount.Should().Be(1990);
        result.Value.Yearly.UnitAmount.Should().Be(19900);
    }

    [Fact]
    public async Task Handle_USUser_UsesUsdPriceIds()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _geoLocationService.GetCountryCodeAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns("US");

        _billingService.GetPriceUnitAmountAsync("price_monthly_usd", Arg.Any<CancellationToken>()).Returns(499);
        _billingService.GetPriceUnitAmountAsync("price_yearly_usd", Arg.Any<CancellationToken>()).Returns(3999);

        var query = new GetPlansQuery(UserId, null, "8.8.8.8");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Currency.Should().Be("usd");
        result.Value.Monthly.UnitAmount.Should().Be(499);
        result.Value.Yearly.UnitAmount.Should().Be(3999);
        result.Value.SavingsPercent.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Handle_ExplicitBrazilCountry_UsesBrlPriceIdsWithoutGeoLookup()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _billingService.GetPriceUnitAmountAsync("price_monthly_brl", Arg.Any<CancellationToken>()).Returns(1990);
        _billingService.GetPriceUnitAmountAsync("price_yearly_brl", Arg.Any<CancellationToken>()).Returns(19900);

        var query = new GetPlansQuery(UserId, "br", "10.0.0.1");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Currency.Should().Be("brl");
        await _geoLocationService.DidNotReceive()
            .GetCountryCodeAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PortugueseProfile_PrefersBrlPricingBeforeGeoFallback()
    {
        var user = CreateTestUser();
        user.SetLanguage("pt-BR");
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _billingService.GetPriceUnitAmountAsync("price_monthly_brl", Arg.Any<CancellationToken>()).Returns(1990);
        _billingService.GetPriceUnitAmountAsync("price_yearly_brl", Arg.Any<CancellationToken>()).Returns(19900);

        var query = new GetPlansQuery(UserId, null, "8.8.8.8");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Currency.Should().Be("brl");
        await _geoLocationService.DidNotReceive()
            .GetCountryCodeAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
