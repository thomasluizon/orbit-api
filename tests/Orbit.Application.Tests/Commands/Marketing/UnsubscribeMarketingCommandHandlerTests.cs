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
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly UnsubscribeMarketingCommandHandler _handler;

    public UnsubscribeMarketingCommandHandlerTests()
    {
        _handler = new UnsubscribeMarketingCommandHandler(
            _tokenService, _userRepo, _unitOfWork, NullLogger<UnsubscribeMarketingCommandHandler>.Instance);
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
    public async Task Handle_AlreadyUnsubscribed_IsIdempotentSuccessWithoutSaving()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetMarketingConsent(false);
        ValidTokenFor(user.Id);
        SetupUserFound(user);

        var result = await _handler.Handle(new UnsubscribeMarketingCommand("valid"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
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
}
