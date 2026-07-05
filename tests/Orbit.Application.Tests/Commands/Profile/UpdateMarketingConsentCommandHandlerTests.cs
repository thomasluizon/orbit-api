using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Profile;

public class UpdateMarketingConsentCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMarketingContactsService _contactsService = Substitute.For<IMarketingContactsService>();
    private readonly UpdateMarketingConsentCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public UpdateMarketingConsentCommandHandlerTests()
    {
        _handler = new UpdateMarketingConsentCommandHandler(
            _userRepo,
            _unitOfWork,
            _contactsService,
            NullLogger<UpdateMarketingConsentCommandHandler>.Instance);
    }

    private void SetupUserFound(User user) =>
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

    [Fact]
    public async Task Handle_OptIn_PersistsConsentAndUpsertsProductContact()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        SetupUserFound(user);

        var result = await _handler.Handle(new UpdateMarketingConsentCommand(UserId, true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.MarketingEmailConsent.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _contactsService.Received(1).UpsertProductContactAsync(
            "test@example.com", Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _contactsService.DidNotReceive().SetContactUnsubscribedAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_OptOut_PersistsConsentAndSetsContactUnsubscribed()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        SetupUserFound(user);

        var result = await _handler.Handle(new UpdateMarketingConsentCommand(UserId, false), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.MarketingEmailConsent.Should().BeFalse();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _contactsService.Received(1).SetContactUnsubscribedAsync(
            "test@example.com", true, Arg.Any<CancellationToken>());
        await _contactsService.DidNotReceive().UpsertProductContactAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailureWithoutSyncing()
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
        await _contactsService.DidNotReceive().UpsertProductContactAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ResendSyncFails_StillPersistsDecisionAndSucceeds()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        SetupUserFound(user);
        _contactsService
            .UpsertProductContactAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Resend unavailable"));

        var result = await _handler.Handle(new UpdateMarketingConsentCommand(UserId, true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.MarketingEmailConsent.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
