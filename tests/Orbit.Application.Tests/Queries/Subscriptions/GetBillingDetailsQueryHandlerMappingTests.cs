using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Subscriptions.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.Subscriptions;

public class GetBillingDetailsQueryHandlerMappingTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IBillingService _billingService = Substitute.For<IBillingService>();
    private readonly ILogger<GetBillingDetailsQueryHandler> _logger = Substitute.For<ILogger<GetBillingDetailsQueryHandler>>();
    private readonly GetBillingDetailsQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetBillingDetailsQueryHandlerMappingTests()
    {
        _handler = new GetBillingDetailsQueryHandler(_userRepo, _billingService, _logger);
    }

    private User CreateSubscribedUser()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        user.SetStripeCustomerId("cus_test");
        user.SetStripeSubscription("sub_test", DateTime.UtcNow.AddDays(30));
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        return user;
    }

    [Fact]
    public async Task Handle_MapsProviderDetailsAndInvoicesFieldForField()
    {
        CreateSubscribedUser();
        var periodEnd = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var invoiceDate = new DateTime(2026, 7, 1, 9, 30, 0, DateTimeKind.Utc);

        _billingService.GetSubscriptionDetailsAsync("sub_test", Arg.Any<CancellationToken>())
            .Returns(new BillingSubscriptionDetails(
                Status: "active",
                CurrentPeriodEnd: periodEnd,
                CancelAtPeriodEnd: true,
                Interval: SubscriptionInterval.Yearly,
                UnitAmount: 7999,
                Currency: "usd",
                PaymentMethod: new BillingPaymentMethod("visa", "4242", 11, 2029)));

        _billingService.ListInvoicesAsync("cus_test", 12, Arg.Any<CancellationToken>())
            .Returns(new List<BillingInvoice>
            {
                new("in_2", invoiceDate.AddMonths(1), 7999, "usd", "paid", "https://host/2", "https://pdf/2", "subscription_cycle"),
                new("in_1", invoiceDate, 7999, "usd", "paid", "https://host/1", "https://pdf/1", "subscription_create"),
            });

        var result = await _handler.Handle(new GetBillingDetailsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var details = result.Value;
        details.Status.Should().Be("active");
        details.CurrentPeriodEnd.Should().Be(periodEnd);
        details.CancelAtPeriodEnd.Should().BeTrue();
        details.Interval.Should().Be("yearly");
        details.AmountPerPeriod.Should().Be(7999);
        details.Currency.Should().Be("usd");

        details.PaymentMethod.Should().NotBeNull();
        details.PaymentMethod!.Brand.Should().Be("visa");
        details.PaymentMethod.Last4.Should().Be("4242");
        details.PaymentMethod.ExpMonth.Should().Be(11);
        details.PaymentMethod.ExpYear.Should().Be(2029);

        details.RecentInvoices.Should().HaveCount(2);
        var firstInvoice = details.RecentInvoices[1];
        firstInvoice.Id.Should().Be("in_1");
        firstInvoice.Date.Should().Be(invoiceDate);
        firstInvoice.AmountPaid.Should().Be(7999);
        firstInvoice.Status.Should().Be("paid");
        firstInvoice.HostedInvoiceUrl.Should().Be("https://host/1");
        firstInvoice.InvoicePdf.Should().Be("https://pdf/1");
        firstInvoice.BillingReason.Should().Be("subscription_create");
    }

    [Fact]
    public async Task Handle_MonthlyIntervalEnum_MapsToLowercaseString()
    {
        CreateSubscribedUser();
        _billingService.GetSubscriptionDetailsAsync("sub_test", Arg.Any<CancellationToken>())
            .Returns(new BillingSubscriptionDetails(
                "active", DateTime.UtcNow.AddDays(15), false,
                SubscriptionInterval.Monthly, 999, "usd", null));
        _billingService.ListInvoicesAsync("cus_test", 12, Arg.Any<CancellationToken>())
            .Returns(new List<BillingInvoice>());

        var result = await _handler.Handle(new GetBillingDetailsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Interval.Should().Be("monthly");
    }

    [Fact]
    public async Task Handle_NullProviderPaymentMethod_MapsToNullDto()
    {
        CreateSubscribedUser();
        _billingService.GetSubscriptionDetailsAsync("sub_test", Arg.Any<CancellationToken>())
            .Returns(new BillingSubscriptionDetails(
                "active", DateTime.UtcNow.AddDays(15), false,
                SubscriptionInterval.Monthly, 999, "usd", PaymentMethod: null));
        _billingService.ListInvoicesAsync("cus_test", 12, Arg.Any<CancellationToken>())
            .Returns(new List<BillingInvoice>());

        var result = await _handler.Handle(new GetBillingDetailsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.PaymentMethod.Should().BeNull();
        result.Value.RecentInvoices.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ProviderReturnsNullSubscription_ReturnsSubscriptionNotFound()
    {
        CreateSubscribedUser();
        _billingService.GetSubscriptionDetailsAsync("sub_test", Arg.Any<CancellationToken>())
            .Returns((BillingSubscriptionDetails?)null);

        var result = await _handler.Handle(new GetBillingDetailsQuery(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.SubscriptionNotFound);
        await _billingService.DidNotReceive().ListInvoicesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
