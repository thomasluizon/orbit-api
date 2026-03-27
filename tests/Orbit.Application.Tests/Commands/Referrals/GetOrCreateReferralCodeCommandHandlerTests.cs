using FluentAssertions;
using NSubstitute;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Referrals;

public class GetOrCreateReferralCodeCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly GetOrCreateReferralCodeCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetOrCreateReferralCodeCommandHandlerTests()
    {
        _handler = new GetOrCreateReferralCodeCommandHandler(_userRepo, _unitOfWork);
    }

    private static User CreateTestUser()
    {
        return User.Create("Test User", "test@example.com").Value;
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
    public async Task Handle_UserWithoutCode_GeneratesAndSavesCode()
    {
        var user = CreateTestUser();
        SetupUserFound(user);

        // No existing user with same code
        _userRepo.FindAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<User>());

        var command = new GetOrCreateReferralCodeCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNullOrEmpty();
        result.Value.Should().HaveLength(8);
        user.ReferralCode.Should().Be(result.Value);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserWithExistingCode_ReturnsExistingCode()
    {
        var user = CreateTestUser();
        user.SetReferralCode("EXISTING");
        SetupUserFound(user);

        var command = new GetOrCreateReferralCodeCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("EXISTING");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        SetupUserNotFound();

        var command = new GetOrCreateReferralCodeCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
    }
}
