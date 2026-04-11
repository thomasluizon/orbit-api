using FluentAssertions;
using NSubstitute;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Profile;

public class CompleteOnboardingCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly CompleteOnboardingCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public CompleteOnboardingCommandHandlerTests()
    {
        _handler = new CompleteOnboardingCommandHandler(_userRepo, _unitOfWork);
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
    public async Task Handle_UserFound_CompletesOnboarding()
    {
        var user = User.Create("Test", "test@test.com").Value;
        SetupUserFound(user);

        var result = await _handler.Handle(new CompleteOnboardingCommand(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.HasCompletedOnboarding.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        SetupUserNotFound();

        var result = await _handler.Handle(new CompleteOnboardingCommand(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
    }
}

public class CompleteTourCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly CompleteTourCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public CompleteTourCommandHandlerTests()
    {
        _handler = new CompleteTourCommandHandler(_userRepo, _unitOfWork);
    }

    [Fact]
    public async Task Handle_UserFound_CompletesTour()
    {
        var user = User.Create("Test", "test@test.com").Value;
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await _handler.Handle(new CompleteTourCommand(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.HasCompletedTour.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _handler.Handle(new CompleteTourCommand(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }
}

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

    [Fact]
    public async Task Handle_UserFound_ResetsTour()
    {
        var user = User.Create("Test", "test@test.com").Value;
        user.CompleteTour();
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await _handler.Handle(new ResetTourCommand(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.HasCompletedTour.Should().BeFalse();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _handler.Handle(new ResetTourCommand(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }
}

public class SetAiMemoryCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly SetAiMemoryCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public SetAiMemoryCommandHandlerTests()
    {
        _handler = new SetAiMemoryCommandHandler(_userRepo, _unitOfWork);
    }

    [Fact]
    public async Task Handle_EnableAiMemory_Success()
    {
        var user = User.Create("Test", "test@test.com").Value;
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await _handler.Handle(new SetAiMemoryCommand(UserId, true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.AiMemoryEnabled.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DisableAiMemory_Success()
    {
        var user = User.Create("Test", "test@test.com").Value;
        user.SetAiMemory(true);
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await _handler.Handle(new SetAiMemoryCommand(UserId, false), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.AiMemoryEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _handler.Handle(new SetAiMemoryCommand(UserId, true), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }
}

public class SetAiSummaryCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly SetAiSummaryCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public SetAiSummaryCommandHandlerTests()
    {
        _handler = new SetAiSummaryCommandHandler(_userRepo, _unitOfWork);
    }

    [Fact]
    public async Task Handle_EnableAiSummary_Success()
    {
        var user = User.Create("Test", "test@test.com").Value;
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await _handler.Handle(new SetAiSummaryCommand(UserId, true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.AiSummaryEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_DisableAiSummary_Success()
    {
        var user = User.Create("Test", "test@test.com").Value;
        user.SetAiSummary(true);
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await _handler.Handle(new SetAiSummaryCommand(UserId, false), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.AiSummaryEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _handler.Handle(new SetAiSummaryCommand(UserId, true), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }
}

public class SetLanguageCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly SetLanguageCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public SetLanguageCommandHandlerTests()
    {
        _handler = new SetLanguageCommandHandler(_userRepo, _unitOfWork);
    }

    [Fact]
    public async Task Handle_ValidLanguage_UpdatesAndSaves()
    {
        var user = User.Create("Test", "test@test.com").Value;
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await _handler.Handle(new SetLanguageCommand(UserId, "pt-BR"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.Language.Should().Be("pt-BR");
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _handler.Handle(new SetLanguageCommand(UserId, "en"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }
}
