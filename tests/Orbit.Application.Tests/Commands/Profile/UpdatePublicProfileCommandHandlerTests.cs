using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Profile;

public class UpdatePublicProfileCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IDistributedCache _cache = Substitute.For<IDistributedCache>();
    private readonly UpdatePublicProfileCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public UpdatePublicProfileCommandHandlerTests()
    {
        _handler = new UpdatePublicProfileCommandHandler(
            _userRepo,
            _unitOfWork,
            _cache,
            Options.Create(new FrontendSettings { BaseUrl = "https://app.useorbit.org" }));
    }

    private static User CreateUser() => User.Create("Ana Clara", "ana@example.com").Value;

    private void SetupUserFound(User user)
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
        _userRepo.AnyAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(false);
    }

    private static UpdatePublicProfileCommand EnableCommand(bool regenerate = false) =>
        new(UserId, Enabled: true, ShowStreak: true, ShowLevel: true, ShowAchievements: true, ShowTopHabits: false, Regenerate: regenerate);

    [Fact]
    public async Task Handle_EnableWithoutSlug_IssuesUnguessableSlugAndShareUrl()
    {
        var user = CreateUser();
        SetupUserFound(user);

        var result = await _handler.Handle(EnableCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Enabled.Should().BeTrue();
        result.Value.Slug.Should().NotBeNullOrWhiteSpace();
        result.Value.Slug!.Length.Should().Be(22);
        result.Value.ShareUrl.Should().Be($"https://app.useorbit.org/u/{result.Value.Slug}");
        user.PublicProfileSlug.Should().Be(result.Value.Slug);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EnableAppliesDefaultsAndFlags()
    {
        var user = CreateUser();
        SetupUserFound(user);
        var command = new UpdatePublicProfileCommand(UserId, Enabled: true, ShowStreak: false, ShowLevel: true, ShowAchievements: false, ShowTopHabits: true, Regenerate: false);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Value.ShowStreak.Should().BeFalse();
        result.Value.ShowLevel.Should().BeTrue();
        result.Value.ShowAchievements.Should().BeFalse();
        result.Value.ShowTopHabits.Should().BeTrue();
        user.PublicProfileShowTopHabits.Should().BeTrue();
        user.PublicProfileShowStreak.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_EnableWithExistingSlug_KeepsSlug()
    {
        var user = CreateUser();
        user.SetPublicProfileSlug("EXISTINGSLUGABCDEF1234");
        SetupUserFound(user);

        var result = await _handler.Handle(EnableCommand(), CancellationToken.None);

        result.Value.Slug.Should().Be("EXISTINGSLUGABCDEF1234");
    }

    [Fact]
    public async Task Handle_Regenerate_RotatesSlugAndEvictsOldCacheEntry()
    {
        var user = CreateUser();
        user.SetPublicProfileSlug("OLDSLUGABCDEFGH1234567");
        SetupUserFound(user);

        var result = await _handler.Handle(EnableCommand(regenerate: true), CancellationToken.None);

        result.Value.Slug.Should().NotBe("OLDSLUGABCDEFGH1234567");
        result.Value.Slug!.Length.Should().Be(22);
        await _cache.Received(1).RemoveAsync("public-profile:OLDSLUGABCDEFGH1234567", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Disable_NullsSlugAndShareUrlAndEvicts()
    {
        var user = CreateUser();
        user.SetPublicProfileSlug("LIVESLUGABCDEFGH123456");
        SetupUserFound(user);
        var command = new UpdatePublicProfileCommand(UserId, Enabled: false, ShowStreak: true, ShowLevel: true, ShowAchievements: true, ShowTopHabits: false, Regenerate: false);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Value.Enabled.Should().BeFalse();
        result.Value.Slug.Should().BeNull();
        result.Value.ShareUrl.Should().BeNull();
        user.PublicProfileSlug.Should().BeNull();
        await _cache.Received(1).RemoveAsync("public-profile:LIVESLUGABCDEFGH123456", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EnableFresh_DoesNotEvictCache()
    {
        var user = CreateUser();
        SetupUserFound(user);

        await _handler.Handle(EnableCommand(), CancellationToken.None);

        await _cache.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailureWithoutSaving()
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _handler.Handle(EnableCommand(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
