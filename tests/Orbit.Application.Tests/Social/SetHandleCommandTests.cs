using System.Data.Common;
using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Common;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Social;

public class SetHandleCommandTests
{
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly SetHandleCommandHandler _handler;

    public SetHandleCommandTests()
    {
        _handler = new SetHandleCommandHandler(_userRepository, _unitOfWork);
    }

    [Fact]
    public async Task Handle_AvailableHandle_SetsHandleAndSaves()
    {
        var user = SocialTestHelpers.OptedInUser();
        SocialTestHelpers.StubUsers(_userRepository, user);
        _userRepository.AnyAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _handler.Handle(new SetHandleCommand(user.Id, "cosmo_42"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.Handle.Should().Be("cosmo_42");
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CaseInsensitiveCollision_ReturnsHandleTaken()
    {
        var user = SocialTestHelpers.OptedInUser();
        _userRepository.FindOneTrackedAsync(
                Arg.Any<Expression<Func<User, bool>>>(),
                Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(user);
        _userRepository.AnyAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await _handler.Handle(new SetHandleCommand(user.Id, "Taken"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.HandleTaken);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UnknownUser_ReturnsUserNotFound()
    {
        SocialTestHelpers.StubUsers(_userRepository);

        var result = await _handler.Handle(new SetHandleCommand(Guid.NewGuid(), "valid_handle"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task Handle_ConcurrentInsertLosesUniqueIndexRace_ReturnsHandleTaken()
    {
        var user = SocialTestHelpers.OptedInUser();
        SocialTestHelpers.StubUsers(_userRepository, user);
        _userRepository.AnyAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>()).Returns(false);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Throws(new DbUpdateException("duplicate handle", new UniqueViolationException()));

        var result = await _handler.Handle(new SetHandleCommand(user.Id, "newhandle"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.HandleTaken);
    }

    private sealed class UniqueViolationException : DbException
    {
        public override string? SqlState => "23505";
    }
}
