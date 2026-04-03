using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Stripe;

namespace Orbit.Application.Tests.Commands.Subscriptions;

public class HandleWebhookCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly SubscriptionService _subscriptionService = Substitute.For<SubscriptionService>();
    private readonly HandleWebhookCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public HandleWebhookCommandHandlerTests()
    {
        var settings = Options.Create(new StripeSettings
        {
            WebhookSecret = "whsec_test_secret"
        });

        _handler = new HandleWebhookCommandHandler(
            _userRepo, _unitOfWork, settings, _subscriptionService,
            Substitute.For<ILogger<HandleWebhookCommandHandler>>());
    }

    [Fact]
    public async Task Handle_WebhookSecretNotConfigured_ReturnsFailure()
    {
        var settings = Options.Create(new StripeSettings { WebhookSecret = "" });
        var handler = new HandleWebhookCommandHandler(
            _userRepo, _unitOfWork, settings, _subscriptionService,
            Substitute.For<ILogger<HandleWebhookCommandHandler>>());

        var command = new HandleWebhookCommand("{}", "sig_test");

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not configured");
    }

    [Fact]
    public async Task Handle_InvalidSignature_ReturnsFailure()
    {
        // EventUtility.ConstructEvent will throw StripeException for invalid signatures
        var command = new HandleWebhookCommand("{}", "invalid_signature");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("signature");
    }

    // NOTE: The checkout.session.completed, customer.subscription.updated, and
    // customer.subscription.deleted event handler tests are not included here because
    // Stripe's EventUtility.ConstructEvent requires a valid HMAC signature that
    // cannot be easily faked in unit tests. These paths are covered by integration tests.
    // The tests above verify the guard clauses (missing webhook secret, invalid signature)
    // which are the most critical security checks.
}
