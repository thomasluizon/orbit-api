using FluentAssertions;
using NSubstitute;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Profile;

public class ProfileCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private static readonly Guid UserId = Guid.NewGuid();

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

    // ----- SetTimezone -----

    [Fact]
    public async Task SetTimezone_Valid_UpdatesAndSaves()
    {
        var user = CreateTestUser();
        SetupUserFound(user);

        var handler = new SetTimezoneCommandHandler(_userRepo, _unitOfWork);
        var command = new SetTimezoneCommand(UserId, "America/New_York");

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.TimeZone.Should().Be("America/New_York");
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetTimezone_UserNotFound_ReturnsFailure()
    {
        SetupUserNotFound();

        var handler = new SetTimezoneCommandHandler(_userRepo, _unitOfWork);
        var command = new SetTimezoneCommand(UserId, "America/New_York");

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("User not found.");
    }

    // ----- SetLanguage -----

    [Fact]
    public async Task SetLanguage_Valid_UpdatesAndSaves()
    {
        var user = CreateTestUser();
        SetupUserFound(user);

        var handler = new SetLanguageCommandHandler(_userRepo, _unitOfWork);
        var command = new SetLanguageCommand(UserId, "pt-BR");

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.Language.Should().Be("pt-BR");
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ----- SetAiMemory -----

    [Fact]
    public async Task SetAiMemory_Valid_UpdatesAndSaves()
    {
        var user = CreateTestUser();
        SetupUserFound(user);

        var handler = new SetAiMemoryCommandHandler(_userRepo, _unitOfWork);
        var command = new SetAiMemoryCommand(UserId, false);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.AiMemoryEnabled.Should().BeFalse();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ----- SetAiSummary -----

    [Fact]
    public async Task SetAiSummary_Valid_UpdatesAndSaves()
    {
        var user = CreateTestUser();
        SetupUserFound(user);

        var handler = new SetAiSummaryCommandHandler(_userRepo, _unitOfWork);
        var command = new SetAiSummaryCommand(UserId, false);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.AiSummaryEnabled.Should().BeFalse();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ----- CompleteOnboarding -----

    [Fact]
    public async Task CompleteOnboarding_Valid_UpdatesAndSaves()
    {
        var user = CreateTestUser();
        SetupUserFound(user);

        var handler = new CompleteOnboardingCommandHandler(_userRepo, _unitOfWork);
        var command = new CompleteOnboardingCommand(UserId);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.HasCompletedOnboarding.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

}
