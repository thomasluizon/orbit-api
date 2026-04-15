using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Common;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Subscriptions;

public class CreatePortalSessionCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IBillingService _billingService = Substitute.For<IBillingService>();
    private readonly CreatePortalSessionCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public CreatePortalSessionCommandHandlerTests()
    {
        var settings = Options.Create(new StripeSettings
        {
            SuccessUrl = "https://app.example.com?subscription=success"
        });

        _handler = new CreatePortalSessionCommandHandler(
            _userRepo, settings, _billingService,
            Substitute.For<ILogger<CreatePortalSessionCommandHandler>>());
    }

    [Fact]
    public async Task Handle_ValidUser_ReturnsPortalUrl()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetStripeCustomerId("cus_123");
        _userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(user);

        _billingService.CreatePortalSessionAsync("cus_123", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("https://billing.stripe.com/portal/session_123");

        var command = new CreatePortalSessionCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Url.Should().Be("https://billing.stripe.com/portal/session_123");
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        var command = new CreatePortalSessionCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task Handle_NoStripeCustomer_ReturnsSubscriptionNotFoundFailure()
    {
        var user = User.Create("Test", "test@example.com").Value;
        _userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(user);

        var command = new CreatePortalSessionCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("subscription");
    }

    [Fact]
    public async Task Handle_BillingProviderException_ReturnsFailure()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetStripeCustomerId("cus_123");
        _userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(user);

        _billingService.CreatePortalSessionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new BillingProviderException("Stripe API error"));

        var command = new CreatePortalSessionCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("temporarily unavailable");
    }
}
