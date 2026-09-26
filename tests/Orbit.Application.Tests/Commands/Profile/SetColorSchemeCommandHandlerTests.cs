using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;
using Orbit.Application.Common;

namespace Orbit.Application.Tests.Commands.Profile;

public class SetColorSchemeCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly SetColorSchemeCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public SetColorSchemeCommandHandlerTests()
    {
        _handler = new SetColorSchemeCommandHandler(_userRepo, _unitOfWork);
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
    public async Task Handle_ValidColorScheme_UpdatesAndSaves()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        SetupUserFound(user);

        var command = new SetColorSchemeCommand(UserId, "purple");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.ColorScheme.Should().Be(ColorSchemes.Granted);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_FreeUser_SavesAccentWithoutProPrompt()
    {
        var user = User.Create("Free User", "free@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        user.HasProAccess.Should().BeFalse();
        SetupUserFound(user);

        var result = await _handler.Handle(new SetColorSchemeCommand(UserId, "blue"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.ColorScheme.Should().Be(ColorSchemes.Granted);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        SetupUserNotFound();

        var command = new SetColorSchemeCommand(UserId, "blue");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.UserNotFound.Message);
    }

    [Theory]
    [InlineData("purple")]
    [InlineData("blue")]
    [InlineData("green")]
    [InlineData("rose")]
    [InlineData("orange")]
    [InlineData("cyan")]
    public async Task Handle_HistoricalColorSchemeFromOldClient_SucceedsAndStoresTheGrantedAccent(string colorScheme)
    {
        var user = User.Create("Test User", "test@example.com").Value;
        SetupUserFound(user);

        var command = new SetColorSchemeCommand(UserId, colorScheme);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.ColorScheme.Should().Be(ColorSchemes.Granted);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NullColorScheme_ClearsTheStoredPreference()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        user.SetColorScheme("blue").IsSuccess.Should().BeTrue();
        SetupUserFound(user);

        var command = new SetColorSchemeCommand(UserId, null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.ColorScheme.Should().BeNull();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UnknownColorScheme_StillRejected()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        SetupUserFound(user);

        var command = new SetColorSchemeCommand(UserId, "magenta");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("INVALID_COLOR_SCHEME");
    }

    [Fact]
    public async Task Handle_ConcurrencyConflictThenSuccess_ResolvesToSuccessAndKeepsLastWrite()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        SetupUserFound(user);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => throw new DbUpdateConcurrencyException("conflict"), _ => 1);

        var command = new SetColorSchemeCommand(UserId, "purple");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.ColorScheme.Should().Be(ColorSchemes.Granted);
        await _userRepo.Received(1).ReloadAsync(user, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
