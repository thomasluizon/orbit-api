using System.Collections.Concurrent;
using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Application.Subscriptions.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;
using Stripe;
using Stripe.Checkout;

namespace Orbit.Infrastructure.Tests.Services;

public partial class StripeBillingServiceTests
{
    [Fact]
    public async Task ParallelCheckouts_ShareUserScopedProviderKey_KeepOneCustomer()
    {
        var remote = new ConcurrentDictionary<string, Customer>();
        var customers = Substitute.For<CustomerService>();
        customers.CreateAsync(Arg.Any<CustomerCreateOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Yield();
                var key = call.Arg<RequestOptions>().IdempotencyKey;
                key.Should().Be($"orbit-customer-create-{UserId}");
                return remote.GetOrAdd(key, _ => new Customer { Id = "cus_shared" });
            });
        var checkout = Substitute.For<SessionService>();
        checkout.CreateAsync(Arg.Any<SessionCreateOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new Session { Url = "https://checkout.stripe.com/session" });
        var billing = new StripeBillingService(new(customers, checkout, new Stripe.BillingPortal.SessionService(),
            new SubscriptionService(), new InvoiceService(), new PriceService(), new CouponService()), NullLogger<StripeBillingService>.Instance);
        var settings = Options.Create(new StripeSettings { YearlyPriceIdUsd = "price_yearly_usd", SuccessUrl = SuccessUrl, CancelUrl = CancelUrl });
        var records = Enumerable.Range(0, 2).Select(_ => User.Create("User", "user@example.com").Value).ToArray();
        var requests = records.Select(user =>
        {
            typeof(User).GetProperty(nameof(User.Id))!.SetValue(user, UserId);
            var repository = Substitute.For<IGenericRepository<User>>();
            repository.FindOneTrackedAsync(Arg.Any<Expression<Func<User, bool>>>(),
                Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>()).Returns(user);
            var handler = new CreateCheckoutCommandHandler(repository, Substitute.For<IUnitOfWork>(),
                Substitute.For<IGeoLocationService>(), settings, billing, new PriceResolver(settings),
                NullLogger<CreateCheckoutCommandHandler>.Instance);
            return handler.Handle(new(UserId, "yearly", "US", null), default);
        }).ToArray();
        (await Task.WhenAll(requests)).Should().OnlyContain(result => result.IsSuccess);
        remote.Should().ContainSingle();
        records.Should().OnlyContain(user => user.StripeCustomerId == "cus_shared");
        await customers.Received(2).CreateAsync(Arg.Any<CustomerCreateOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>());
    }
}
