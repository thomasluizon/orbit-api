using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Profile;

public class ResetTourCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ResetTourCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public ResetTourCommandHandlerTests()
    {
        _handler = new ResetTourCommandHandler(_userRepo, _unitOfWork);
    }

    private void SetupUserFound(User user)
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
    }

    private void SetupUserNotFound()
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null);
    }

    [Fact]
    public async Task Handle_UserFound_ResetsTourAndSaves()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        user.CompleteTour();
        user.HasCompletedTour.Should().BeTrue();
        SetupUserFound(user);

        var result = await _handler.Handle(new ResetTourCommand(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.HasCompletedTour.Should().BeFalse();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        SetupUserNotFound();

        var result = await _handler.Handle(new ResetTourCommand(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.UserNotFound);
        result.ErrorCode.Should().Be(ErrorCodes.UserNotFound);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
