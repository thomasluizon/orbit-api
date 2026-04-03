using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Subscriptions.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Stripe;

namespace Orbit.Application.Tests.Queries.Subscriptions;

public class GetBillingDetailsQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly SubscriptionService _subscriptionService = Substitute.For<SubscriptionService>();
    private readonly InvoiceService _invoiceService = Substitute.For<InvoiceService>();
    private readonly ILogger<GetBillingDetailsQueryHandler> _logger = Substitute.For<ILogger<GetBillingDetailsQueryHandler>>();
    private readonly GetBillingDetailsQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetBillingDetailsQueryHandlerTests()
    {
        _handler = new GetBillingDetailsQueryHandler(_userRepo, _subscriptionService, _invoiceService, _logger);
    }

    private static User CreateTestUser()
    {
        return User.Create("Test User", "test@example.com").Value;
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var query = new GetBillingDetailsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
        result.ErrorCode.Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_FreeUser_NoSubscription_ReturnsFailure()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var query = new GetBillingDetailsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("No active subscription found");
    }

    [Fact]
    public async Task Handle_UserWithSubscription_StripeError_ReturnsFailure()
    {
        var user = CreateTestUser();
        user.SetStripeCustomerId("cus_test");
        user.SetStripeSubscription("sub_test", DateTime.UtcNow.AddDays(30));

        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _subscriptionService.GetAsync(
            Arg.Any<string>(),
            Arg.Any<SubscriptionGetOptions>(),
            Arg.Any<RequestOptions>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new StripeException("Stripe error"));

        var query = new GetBillingDetailsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Failed to load billing details");
    }
}
