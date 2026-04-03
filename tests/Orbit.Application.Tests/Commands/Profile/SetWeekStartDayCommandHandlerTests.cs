using FluentAssertions;
using NSubstitute;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Profile;

public class SetWeekStartDayCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly SetWeekStartDayCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public SetWeekStartDayCommandHandlerTests()
    {
        _handler = new SetWeekStartDayCommandHandler(_userRepo, _unitOfWork);
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
    public async Task Handle_ValidDay_UpdatesAndSaves()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        SetupUserFound(user);

        var command = new SetWeekStartDayCommand(UserId, 0);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.WeekStartDay.Should().Be(0);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        SetupUserNotFound();

        var command = new SetWeekStartDayCommand(UserId, 0);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("User not found.");
    }
}
