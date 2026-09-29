using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Profile;

public class SetClockFormatCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly SetClockFormatCommandHandler _handler;
    private static readonly Guid UserId = Guid.NewGuid();

    public SetClockFormatCommandHandlerTests() => _handler = new SetClockFormatCommandHandler(_userRepo, _unitOfWork);

    [Fact]
    public async Task Handle_StoresChoiceAndSavesOnce()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>()).Returns(user);

        var result = await _handler.Handle(new SetClockFormatCommand(UserId, false), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.Uses24HourClockPreference.Should().BeFalse();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UnknownUserReturnsUserNotFoundWithoutSaving()
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _handler.Handle(new SetClockFormatCommand(UserId, true), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.UserNotFound.Message);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
