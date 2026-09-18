using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Behaviors;

/// <summary>
/// The colour-scheme write and the colour-scheme read have to agree. A shipped client sends a
/// scheme, paints the accent from its own optimistic cache, then refetches the profile. If the read
/// answers a different value from the one the write stored, the accent snaps back and the tap looks
/// like it did nothing.
/// </summary>
public class ColorSchemeRoundTripTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<StreakFreeze> _streakFreezeRepo = Substitute.For<IGenericRepository<StreakFreeze>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IFeatureFlagService _featureFlagService = Substitute.For<IFeatureFlagService>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly SetColorSchemeCommandHandler _writeHandler;
    private readonly GetProfileQueryHandler _readHandler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 9, 18);

    public ColorSchemeRoundTripTests()
    {
        _payGate.CanManagePremiumColors(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        _payGate.GetAiMessageLimit(UserId, Arg.Any<CancellationToken>()).Returns(20);
        _featureFlagService.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());

        _writeHandler = new SetColorSchemeCommandHandler(_userRepo, _payGate, _unitOfWork);
        _readHandler = new GetProfileQueryHandler(
            _userRepo,
            _streakFreezeRepo,
            _userDateService,
            _featureFlagService,
            _payGate,
            Options.Create(new FrontendSettings()));
    }

    private User ArrangeUser()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        return user;
    }

    [Theory]
    [InlineData("purple")]
    [InlineData("blue")]
    [InlineData("green")]
    [InlineData("rose")]
    [InlineData("orange")]
    [InlineData("cyan")]
    public async Task SetThenGet_ReturnsTheValueTheWriteStored(string requested)
    {
        var user = ArrangeUser();

        var write = await _writeHandler.Handle(new SetColorSchemeCommand(UserId, requested), CancellationToken.None);
        write.IsSuccess.Should().BeTrue();

        var read = await _readHandler.Handle(new GetProfileQuery(UserId), CancellationToken.None);

        read.IsSuccess.Should().BeTrue();
        read.Value.ColorScheme.Should().Be(user.ColorScheme);
        read.Value.ColorScheme.Should().Be(ColorSchemes.Granted);
    }

    [Fact]
    public async Task SetNullThenGet_ClearsTheStoredPreferenceAndStillReadsTheGrantedAccent()
    {
        var user = ArrangeUser();

        var write = await _writeHandler.Handle(new SetColorSchemeCommand(UserId, null), CancellationToken.None);
        write.IsSuccess.Should().BeTrue();

        var read = await _readHandler.Handle(new GetProfileQuery(UserId), CancellationToken.None);

        read.IsSuccess.Should().BeTrue();
        user.ColorScheme.Should().BeNull();
        read.Value.ColorScheme.Should().Be(ColorSchemes.Granted);
    }
}
