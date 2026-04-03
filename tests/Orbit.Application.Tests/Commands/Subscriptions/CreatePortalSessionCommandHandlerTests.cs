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
using Stripe.BillingPortal;

namespace Orbit.Application.Tests.Commands.Subscriptions;

public class CreatePortalSessionCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly SessionService _portalSessionService = Substitute.For<SessionService>();
    private readonly CreatePortalSessionCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public CreatePortalSessionCommandHandlerTests()
    {
        var settings = Options.Create(new StripeSettings
        {
            SuccessUrl = "https://app.example.com?subscription=success"
        });

        _handler = new CreatePortalSessionCommandHandler(
            _userRepo, settings, _portalSessionService,
            Substitute.For<ILogger<CreatePortalSessionCommandHandler>>());
    }

    [Fact]
    public async Task Handle_ValidUser_ReturnsPortalUrl()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetStripeCustomerId("cus_123");
        _userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(user);

        var session = new Session { Url = "https://billing.stripe.com/portal/session_123" };
        _portalSessionService.CreateAsync(
            Arg.Any<SessionCreateOptions>(),
            Arg.Any<RequestOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(session);

        var command = new CreatePortalSessionCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Url.Should().Be("https://billing.stripe.com/portal/session_123");
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        // GetByIdAsync returns null by default
        var command = new CreatePortalSessionCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task Handle_NoStripeCustomer_ReturnsSubscriptionNotFoundFailure()
    {
        var user = User.Create("Test", "test@example.com").Value;
        // User has no StripeCustomerId
        _userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(user);

        var command = new CreatePortalSessionCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("subscription");
    }

    [Fact]
    public async Task Handle_StripeApiError_ReturnsFailure()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetStripeCustomerId("cus_123");
        _userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(user);

        _portalSessionService.CreateAsync(
            Arg.Any<SessionCreateOptions>(),
            Arg.Any<RequestOptions>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new StripeException("Stripe API error"));

        var command = new CreatePortalSessionCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("temporarily unavailable");
    }
}
