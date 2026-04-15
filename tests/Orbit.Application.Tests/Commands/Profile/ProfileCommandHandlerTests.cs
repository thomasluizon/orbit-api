using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Profile;

public class ProfileCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();

    private static readonly Guid UserId = Guid.NewGuid();

    private static User CreateTestUser()
    {
        return User.Create("Test User", "test@example.com").Value;
    }

    public ProfileCommandHandlerTests()
    {
        _payGate.CanManageAiMemory(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        _payGate.CanManageAiSummary(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        _payGate.CanManagePremiumColors(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
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

        var handler = new SetTimezoneCommandHandler(_userRepo, _unitOfWork, _userDateService);
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

        var handler = new SetTimezoneCommandHandler(_userRepo, _unitOfWork, _userDateService);
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

        var handler = new SetAiMemoryCommandHandler(_userRepo, _payGate, _unitOfWork);
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

        var handler = new SetAiSummaryCommandHandler(_userRepo, _payGate, _unitOfWork);
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

    // ----- SetColorScheme -----

    [Fact]
    public async Task SetColorScheme_Valid_UpdatesAndSaves()
    {
        var user = CreateTestUser();
        SetupUserFound(user);

        var handler = new SetColorSchemeCommandHandler(_userRepo, _payGate, _unitOfWork);
        var command = new SetColorSchemeCommand(UserId, "purple");

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.ColorScheme.Should().Be("purple");
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetColorScheme_UserNotFound_ReturnsFailure()
    {
        SetupUserNotFound();

        var handler = new SetColorSchemeCommandHandler(_userRepo, _payGate, _unitOfWork);
        var command = new SetColorSchemeCommand(UserId, "purple");

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("User not found.");
    }

    [Fact]
    public async Task SetColorScheme_Null_ClearsScheme()
    {
        var user = CreateTestUser();
        SetupUserFound(user);

        var handler = new SetColorSchemeCommandHandler(_userRepo, _payGate, _unitOfWork);
        var command = new SetColorSchemeCommand(UserId, null);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.ColorScheme.Should().BeNull();
    }

    // ----- SetThemePreference -----

    [Fact]
    public async Task SetThemePreference_Valid_UpdatesAndSaves()
    {
        var user = CreateTestUser();
        SetupUserFound(user);

        var handler = new SetThemePreferenceCommandHandler(_userRepo, _unitOfWork);
        var command = new SetThemePreferenceCommand(UserId, "dark");

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.ThemePreference.Should().Be("dark");
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetThemePreference_UserNotFound_ReturnsFailure()
    {
        SetupUserNotFound();

        var handler = new SetThemePreferenceCommandHandler(_userRepo, _unitOfWork);
        var command = new SetThemePreferenceCommand(UserId, "dark");

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("User not found.");
    }

    [Fact]
    public async Task SetThemePreference_Null_ClearsPreference()
    {
        var user = CreateTestUser();
        SetupUserFound(user);

        var handler = new SetThemePreferenceCommandHandler(_userRepo, _unitOfWork);
        var command = new SetThemePreferenceCommand(UserId, null);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.ThemePreference.Should().BeNull();
    }

    // ----- SetWeekStartDay -----

    [Fact]
    public async Task SetWeekStartDay_Valid_UpdatesAndSaves()
    {
        var user = CreateTestUser();
        SetupUserFound(user);

        var handler = new SetWeekStartDayCommandHandler(_userRepo, _unitOfWork);
        var command = new SetWeekStartDayCommand(UserId, 0);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.WeekStartDay.Should().Be(0);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetWeekStartDay_UserNotFound_ReturnsFailure()
    {
        SetupUserNotFound();

        var handler = new SetWeekStartDayCommandHandler(_userRepo, _unitOfWork);
        var command = new SetWeekStartDayCommand(UserId, 0);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("User not found.");
    }

    // ----- ResetAccount -----

    [Fact]
    public async Task ResetAccount_Valid_ResetsAndSaves()
    {
        var user = CreateTestUser();
        SetupUserFound(user);
        var accountResetRepo = Substitute.For<IAccountResetRepository>();

        var handler = new ResetAccountCommandHandler(_userRepo, accountResetRepo, _unitOfWork, _cache);
        var command = new ResetAccountCommand(UserId);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await accountResetRepo.Received(1).DeleteAllUserDataAsync(UserId, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).CommitTransactionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResetAccount_UserNotFound_ReturnsFailure()
    {
        SetupUserNotFound();
        var accountResetRepo = Substitute.For<IAccountResetRepository>();

        var handler = new ResetAccountCommandHandler(_userRepo, accountResetRepo, _unitOfWork, _cache);
        var command = new ResetAccountCommand(UserId);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("User not found.");
        await accountResetRepo.DidNotReceive().DeleteAllUserDataAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ----- RequestAccountDeletion -----

    [Fact]
    public async Task RequestAccountDeletion_Valid_SendsCodeAndSucceeds()
    {
        var user = CreateTestUser();
        var emailService = Substitute.For<IEmailService>();

        _userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(user);

        var handler = new RequestAccountDeletionCommandHandler(_cache, _userRepo, emailService);
        var command = new RequestAccountDeletionCommand(UserId);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await emailService.Received(1).SendAccountDeletionCodeAsync(
            user.Email,
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequestAccountDeletion_UserNotFound_ReturnsFailure()
    {
        var emailService = Substitute.For<IEmailService>();
        _userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((User?)null);

        var handler = new RequestAccountDeletionCommandHandler(_cache, _userRepo, emailService);
        var command = new RequestAccountDeletionCommand(UserId);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("User not found.");
        await emailService.DidNotReceive().SendAccountDeletionCodeAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
