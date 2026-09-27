using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Marketing.Commands;
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
    public async Task Handle_OptOutThenOptIn_RestoresBroadcastDelivery()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        SetupUserFound(user);
        MarketingContact? contact = null;
        SetupContactStorage(() => contact, value => contact = value);

        await _handler.Handle(new UpdateMarketingConsentCommand(UserId, false), CancellationToken.None);
        contact.Should().NotBeNull();
        contact!.Source.Should().Be("user");

        await _handler.Handle(new UpdateMarketingConsentCommand(UserId, true), CancellationToken.None);

        user.MarketingEmailConsent.Should().BeTrue();
        contact.UnsubscribedAtUtc.Should().BeNull();
        (await BroadcastRecipientCountAsync(user, contact)).Should().Be(1);
    }

    [Fact]
    public async Task Handle_OptIn_DoesNotClearSuppression()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        var contact = MarketingContact.RecordUserOptOut(user.Email);
        contact.Suppress();
        SetupUserFound(user);
        SetupContactStorage(() => contact, _ => throw new InvalidOperationException("Unexpected contact insert"));

        await _handler.Handle(new UpdateMarketingConsentCommand(UserId, true), CancellationToken.None);

        contact.UnsubscribedAtUtc.Should().BeNull();
        contact.SuppressedAtUtc.Should().NotBeNull();
        (await BroadcastRecipientCountAsync(user, contact)).Should().Be(0);
    }

    [Fact]
    public async Task Handle_OptIn_DoesNotResubscribeWaitlistContact()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        var contact = MarketingContact.ConfirmWaitlist(user.Email, "en");
        contact.Unsubscribe();
        SetupUserFound(user);
        SetupContactStorage(() => contact, _ => throw new InvalidOperationException("Unexpected contact insert"));

        await _handler.Handle(new UpdateMarketingConsentCommand(UserId, true), CancellationToken.None);

        contact.UnsubscribedAtUtc.Should().NotBeNull();
        (await BroadcastRecipientCountAsync(user, contact)).Should().Be(0);
    }

    private void SetupContactStorage(Func<MarketingContact?> current, Action<MarketingContact> add)
    {
        _contactRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<MarketingContact, bool>>>(),
            Arg.Any<Func<IQueryable<MarketingContact>, IQueryable<MarketingContact>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var contact = current();
                return contact is not null && call.Arg<Expression<Func<MarketingContact, bool>>>().Compile()(contact)
                    ? contact : null;
            });
        _contactRepo.AddAsync(Arg.Any<MarketingContact>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                add(call.Arg<MarketingContact>());
                return Task.CompletedTask;
            });
    }

    private async Task<int> BroadcastRecipientCountAsync(User user, MarketingContact contact)
    {
        _userRepo.FindIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>()).Returns(new[] { user });
        _contactRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[] { contact });

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IEmailService)).Returns(Substitute.For<IEmailService>());
        provider.GetService(typeof(IMarketingUnsubscribeTokenService))
            .Returns(Substitute.For<IMarketingUnsubscribeTokenService>());
        provider.GetService(typeof(ILogger<SendMarketingBroadcastCommandHandler>))
            .Returns(NullLogger<SendMarketingBroadcastCommandHandler>.Instance);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);
        var broadcast = new SendMarketingBroadcastCommandHandler(
            _userRepo, _contactRepo, Substitute.For<Hangfire.IBackgroundJobClient>(),
            Substitute.For<IMarketingUnsubscribeTokenService>(), scopeFactory,
            Options.Create(new MarketingSettings { ApiBaseUrl = "https://api.useorbit.org", SendDelayMilliseconds = 0 }),
            NullLogger<SendMarketingBroadcastCommandHandler>.Instance);

        var result = await broadcast.Handle(
            new SendMarketingBroadcastCommand("Subject", "Assunto", "<p>Body</p>", "<p>Corpo</p>", null),
            CancellationToken.None);
        return result.Value.RecipientCount;
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
