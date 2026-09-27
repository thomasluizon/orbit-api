using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Profile;

public class UpdateMarketingConsentCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<MarketingContact> _contactRepo = Substitute.For<IGenericRepository<MarketingContact>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly UpdateMarketingConsentCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public UpdateMarketingConsentCommandHandlerTests()
    {
        _unitOfWork.ExecuteInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        _handler = new UpdateMarketingConsentCommandHandler(_userRepo, _contactRepo, _unitOfWork);
    }

    private void SetupUserFound(User user) =>
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

    [Fact]
    public async Task Handle_OptIn_PersistsConsent()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        SetupUserFound(user);

        var result = await _handler.Handle(new UpdateMarketingConsentCommand(UserId, true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.MarketingEmailConsent.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_OptOut_PersistsConsent()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        SetupUserFound(user);

        var result = await _handler.Handle(new UpdateMarketingConsentCommand(UserId, false), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.MarketingEmailConsent.Should().BeFalse();
        await _contactRepo.Received(1).AddAsync(
            Arg.Is<MarketingContact>(contact => contact.Email == "test@example.com" && contact.UnsubscribedAtUtc != null),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_OptOut_PreservesExistingWaitlistContact()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        var contact = MarketingContact.ConfirmWaitlist("test@example.com", "pt-BR");
        SetupUserFound(user);
        _contactRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<MarketingContact, bool>>>(),
            Arg.Any<Func<IQueryable<MarketingContact>, IQueryable<MarketingContact>>?>(),
            Arg.Any<CancellationToken>()).Returns(contact);

        var result = await _handler.Handle(new UpdateMarketingConsentCommand(UserId, false), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        contact.UnsubscribedAtUtc.Should().NotBeNull();
        contact.Source.Should().Be("waitlist");
        await _contactRepo.DidNotReceive().AddAsync(Arg.Any<MarketingContact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailureWithoutSaving()
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _handler.Handle(new UpdateMarketingConsentCommand(UserId, true), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("User not found.");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
