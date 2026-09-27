using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Marketing.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Marketing;

public class UnsubscribeMarketingCommandHandlerTests
{
    private readonly IMarketingUnsubscribeTokenService _tokenService = Substitute.For<IMarketingUnsubscribeTokenService>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<MarketingContact> _contactRepo = Substitute.For<IGenericRepository<MarketingContact>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly UnsubscribeMarketingCommandHandler _handler;

    public UnsubscribeMarketingCommandHandlerTests()
    {
        _unitOfWork.ExecuteInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        _handler = new UnsubscribeMarketingCommandHandler(
            _tokenService, _userRepo, _contactRepo, _unitOfWork, NullLogger<UnsubscribeMarketingCommandHandler>.Instance);
    }

    private void ValidTokenFor(Guid userId) =>
        _tokenService
            .TryValidateToken(Arg.Any<string>(), out Arg.Any<Guid>())
            .Returns(callInfo =>
            {
                callInfo[1] = userId;
                return true;
            });

    private void SetupUserFound(User? user) =>
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

    [Fact]
    public async Task Handle_ValidToken_FlipsConsentToFalse()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetMarketingConsent(true);
        ValidTokenFor(user.Id);
        SetupUserFound(user);

        var result = await _handler.Handle(new UnsubscribeMarketingCommand("valid"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.MarketingEmailConsent.Should().BeFalse();
        await _contactRepo.Received(1).AddAsync(
            Arg.Is<MarketingContact>(contact => contact.Email == "test@example.com" &&
                contact.Source == "user" && contact.UnsubscribedAtUtc != null),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvalidToken_RejectedWithNoStateChange()
    {
        _tokenService.TryValidateToken(Arg.Any<string>(), out Arg.Any<Guid>()).Returns(false);

        var result = await _handler.Handle(new UnsubscribeMarketingCommand("tampered"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        await _userRepo.DidNotReceive().FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AlreadyUnsubscribed_RecordsAddressOptOut()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetMarketingConsent(false);
        ValidTokenFor(user.Id);
        SetupUserFound(user);

        var result = await _handler.Handle(new UnsubscribeMarketingCommand("valid"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _contactRepo.Received(1).AddAsync(
            Arg.Is<MarketingContact>(contact => contact.Email == "test@example.com" && contact.UnsubscribedAtUtc != null),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UnknownUser_IsIdempotentSuccess()
    {
        ValidTokenFor(Guid.NewGuid());
        SetupUserFound(null);

        var result = await _handler.Handle(new UnsubscribeMarketingCommand("valid"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WaitlistContact_MarksUnsubscribed()
    {
        var contact = MarketingContact.ConfirmWaitlist("person@example.com", "en");
        ValidTokenFor(contact.Id);
        SetupUserFound(null);
        _contactRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<MarketingContact, bool>>>(),
            Arg.Any<Func<IQueryable<MarketingContact>, IQueryable<MarketingContact>>?>(),
            Arg.Any<CancellationToken>()).Returns(contact);

        var result = await _handler.Handle(new UnsubscribeMarketingCommand("valid"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        contact.UnsubscribedAtUtc.Should().NotBeNull();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserWithWaitlistContact_MarksContactUnsubscribed()
    {
        var user = User.Create("Test", "TEST@example.com").Value;
        user.SetMarketingConsent(true);
        var contact = MarketingContact.ConfirmWaitlist("test@example.com", "en");
        ValidTokenFor(user.Id);
        SetupUserFound(user);
        _contactRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<MarketingContact, bool>>>(),
            Arg.Any<Func<IQueryable<MarketingContact>, IQueryable<MarketingContact>>?>(),
            Arg.Any<CancellationToken>()).Returns(contact);

        var result = await _handler.Handle(new UnsubscribeMarketingCommand("valid"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.MarketingEmailConsent.Should().BeFalse();
        contact.UnsubscribedAtUtc.Should().NotBeNull();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
