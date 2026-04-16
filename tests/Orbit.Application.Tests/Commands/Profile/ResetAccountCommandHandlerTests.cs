using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Profile;

public class ResetAccountCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IAccountResetRepository _accountResetRepo = Substitute.For<IAccountResetRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly ResetAccountCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public ResetAccountCommandHandlerTests()
    {
        _handler = new ResetAccountCommandHandler(_userRepo, _accountResetRepo, _unitOfWork, _cache);
        _unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var operation = call.ArgAt<Func<CancellationToken, Task>>(0);
                var ct = call.ArgAt<CancellationToken>(1);
                return operation(ct);
            });
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
    public async Task Handle_UserFound_ResetsAccountAndSaves()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        SetupUserFound(user);

        var command = new ResetAccountCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.HasCompletedOnboarding.Should().BeFalse();
        user.TotalXp.Should().Be(0);
        user.Level.Should().Be(1);
        await _accountResetRepo.Received(1).DeleteAllUserDataAsync(UserId, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        SetupUserNotFound();

        var command = new ResetAccountCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("User not found.");
    }
}
