using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Resend.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Resend;

public class HandleResendWebhookCommandHandlerTests
{
    private readonly IResendWebhookVerifier _verifier = Substitute.For<IResendWebhookVerifier>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly HandleResendWebhookCommandHandler _handler;

    private const string UnsubscribePayload =
        """{"type":"contact.updated","created_at":"2024-10-11T23:47:56.678Z","data":{"id":"c1","email":"unsub@example.com","unsubscribed":true}}""";

    public HandleResendWebhookCommandHandlerTests()
    {
        _handler = new HandleResendWebhookCommandHandler(
            _verifier,
            _userRepo,
            _unitOfWork,
            NullLogger<HandleResendWebhookCommandHandler>.Instance);
    }

    private void VerifierReturns(ResendWebhookVerification verification) =>
        _verifier.Verify(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(verification);

    private void UserFound(User user) =>
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

    private void UserNotFound() =>
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null);

    private Task<Result> Handle(string payload = UnsubscribePayload) =>
        _handler.Handle(new HandleResendWebhookCommand(payload, "id", "ts", "v1,sig"), CancellationToken.None);

    [Fact]
    public async Task Handle_ValidUnsubscribeEvent_RevokesConsent()
    {
        var user = User.Create("Test", "unsub@example.com").Value;
        user.SetMarketingConsent(true);
        VerifierReturns(ResendWebhookVerification.Verified);
        UserFound(user);

        var result = await Handle();

        result.IsSuccess.Should().BeTrue();
        user.MarketingEmailConsent.Should().BeFalse();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvalidSignature_ReturnsFailureWithoutSaving()
    {
        VerifierReturns(ResendWebhookVerification.InvalidSignature);

        var result = await Handle();

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidResendWebhookSignature);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SecretNotConfigured_ReturnsFailureWithoutSaving()
    {
        VerifierReturns(ResendWebhookVerification.SecretNotConfigured);

        var result = await Handle();

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ResendWebhookSecretNotConfigured);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UnknownEmail_ReturnsSuccessNoOp()
    {
        VerifierReturns(ResendWebhookVerification.Verified);
        UserNotFound();

        var result = await Handle();

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NonContactUpdatedEvent_IgnoredWithoutLookup()
    {
        VerifierReturns(ResendWebhookVerification.Verified);

        var result = await Handle(
            """{"type":"email.delivered","data":{"email":"x@example.com"}}""");

        result.IsSuccess.Should().BeTrue();
        await _userRepo.DidNotReceive().FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ContactUpdatedButStillSubscribed_IgnoredNoOp()
    {
        VerifierReturns(ResendWebhookVerification.Verified);

        var result = await Handle(
            """{"type":"contact.updated","data":{"email":"x@example.com","unsubscribed":false}}""");

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DoubleFire_IsIdempotent()
    {
        var user = User.Create("Test", "unsub@example.com").Value;
        user.SetMarketingConsent(false);
        VerifierReturns(ResendWebhookVerification.Verified);
        UserFound(user);

        var first = await Handle();
        var second = await Handle();

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        user.MarketingEmailConsent.Should().BeFalse();
    }
}
