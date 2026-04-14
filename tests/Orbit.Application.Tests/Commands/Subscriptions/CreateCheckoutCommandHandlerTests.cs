using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Common;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Stripe;
using Stripe.Checkout;

namespace Orbit.Application.Tests.Commands.Subscriptions;

public class CreateCheckoutCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IGeoLocationService _geoLocationService = Substitute.For<IGeoLocationService>();
    private readonly CustomerService _customerService = Substitute.For<CustomerService>();
    private readonly SessionService _sessionService = Substitute.For<SessionService>();
    private readonly CreateCheckoutCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public CreateCheckoutCommandHandlerTests()
    {
        var settings = Options.Create(new StripeSettings
        {
            MonthlyPriceIdUsd = "price_monthly_usd",
            YearlyPriceIdUsd = "price_yearly_usd",
            MonthlyPriceIdBrl = "price_monthly_brl",
            YearlyPriceIdBrl = "price_yearly_brl",
            SuccessUrl = "https://app.example.com/success",
            CancelUrl = "https://app.example.com/cancel"
        });

        _handler = new CreateCheckoutCommandHandler(
            _userRepo, _unitOfWork, _geoLocationService, settings,
            _customerService, _sessionService,
            Substitute.For<ILogger<CreateCheckoutCommandHandler>>());

        _geoLocationService.GetCountryCodeAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("US");
    }

    [Fact]
    public async Task Handle_ValidMonthlyCheckout_ReturnsSessionUrl()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetStripeCustomerId("cus_existing");
        SetupExistingUser(user);

        _sessionService.CreateAsync(
            Arg.Any<SessionCreateOptions>(),
            Arg.Any<RequestOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(new Session { Url = "https://checkout.stripe.com/session_123" });

        var command = new CreateCheckoutCommand(UserId, "monthly", null, "1.2.3.4");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Url.Should().Be("https://checkout.stripe.com/session_123");
    }

    [Fact]
    public async Task Handle_NewStripeCustomer_CreatesCustomerFirst()
    {
        var user = User.Create("Test", "test@example.com").Value;
        SetupExistingUser(user);

        _customerService.CreateAsync(
            Arg.Any<CustomerCreateOptions>(),
            Arg.Any<RequestOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(new Customer { Id = "cus_new_123" });

        _sessionService.CreateAsync(
            Arg.Any<SessionCreateOptions>(),
            Arg.Any<RequestOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(new Session { Url = "https://checkout.stripe.com/session_456" });

        var command = new CreateCheckoutCommand(UserId, "monthly", null, null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _customerService.Received(1).CreateAsync(
            Arg.Any<CustomerCreateOptions>(),
            Arg.Any<RequestOptions>(),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        var command = new CreateCheckoutCommand(UserId, "monthly", null, null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task Handle_InvalidInterval_ReturnsFailure()
    {
        var user = User.Create("Test", "test@example.com").Value;
        SetupExistingUser(user);

        var command = new CreateCheckoutCommand(UserId, "weekly", null, null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("billing interval");
    }

    [Fact]
    public async Task Handle_BrazilianUser_UsesBrlPriceId()
    {
        _geoLocationService.GetCountryCodeAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("BR");

        var user = User.Create("Test", "test@example.com").Value;
        user.SetStripeCustomerId("cus_br");
        SetupExistingUser(user);

        _sessionService.CreateAsync(
            Arg.Any<SessionCreateOptions>(),
            Arg.Any<RequestOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(new Session { Url = "https://checkout.stripe.com/br_session" });

        var command = new CreateCheckoutCommand(UserId, "yearly", null, "200.100.50.1");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _sessionService.Received(1).CreateAsync(
            Arg.Is<SessionCreateOptions>(o => o.LineItems[0].Price == "price_yearly_brl"),
            Arg.Any<RequestOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_StripeException_ReturnsFailure()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetStripeCustomerId("cus_err");
        SetupExistingUser(user);

        _sessionService.CreateAsync(
            Arg.Any<SessionCreateOptions>(),
            Arg.Any<RequestOptions>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new StripeException("API error"));

        var command = new CreateCheckoutCommand(UserId, "monthly", null, null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("temporarily unavailable");
    }

    [Fact]
    public async Task Handle_ExplicitBrazilCountry_UsesBrlPriceIdWithoutGeoLookup()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetStripeCustomerId("cus_br_header");
        SetupExistingUser(user);

        _sessionService.CreateAsync(
            Arg.Any<SessionCreateOptions>(),
            Arg.Any<RequestOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(new Session { Url = "https://checkout.stripe.com/br_header" });

        var command = new CreateCheckoutCommand(UserId, "monthly", "BR", "10.0.0.1");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _sessionService.Received(1).CreateAsync(
            Arg.Is<SessionCreateOptions>(o => o.LineItems[0].Price == "price_monthly_brl"),
            Arg.Any<RequestOptions>(),
            Arg.Any<CancellationToken>());
        await _geoLocationService.DidNotReceive()
            .GetCountryCodeAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_BrazilTimezoneProfile_UsesBrlPriceIdBeforeGeoFallback()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetStripeCustomerId("cus_br_tz");
        user.SetTimeZone("America/Sao_Paulo").IsSuccess.Should().BeTrue();
        SetupExistingUser(user);

        _sessionService.CreateAsync(
            Arg.Any<SessionCreateOptions>(),
            Arg.Any<RequestOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(new Session { Url = "https://checkout.stripe.com/br_tz" });

        var command = new CreateCheckoutCommand(UserId, "monthly", null, "8.8.8.8");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _sessionService.Received(1).CreateAsync(
            Arg.Is<SessionCreateOptions>(o => o.LineItems[0].Price == "price_monthly_brl"),
            Arg.Any<RequestOptions>(),
            Arg.Any<CancellationToken>());
        await _geoLocationService.DidNotReceive()
            .GetCountryCodeAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    private void SetupExistingUser(User user)
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
    }
}
