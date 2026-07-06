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
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly UpdateMarketingConsentCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public UpdateMarketingConsentCommandHandlerTests()
    {
        _handler = new UpdateMarketingConsentCommandHandler(_userRepo, _unitOfWork);
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
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
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
