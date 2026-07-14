using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Profile;

public class DismissImportPromptCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly DismissImportPromptCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public DismissImportPromptCommandHandlerTests() =>
        _handler = new DismissImportPromptCommandHandler(_userRepo, _unitOfWork);

    private void SetupUserFound(User user) =>
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

    [Fact]
    public async Task Handle_MarksPromptSeenAndSaves()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        user.HasSeenImportPrompt.Should().BeFalse();
        SetupUserFound(user);

        var result = await _handler.Handle(new DismissImportPromptCommand(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.HasSeenImportPrompt.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailureAndDoesNotSave()
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _handler.Handle(new DismissImportPromptCommand(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("User not found.");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
