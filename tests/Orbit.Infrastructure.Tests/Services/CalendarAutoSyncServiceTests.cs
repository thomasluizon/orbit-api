using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Calendar.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class CalendarAutoSyncServiceTests : IDisposable
{
    private static readonly DateTime NowUtc = new(2100, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly List<Guid> _dispatchedUserIds = [];
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private readonly OrbitDbContext _db;
    private readonly CalendarAutoSyncService _service;

    public CalendarAutoSyncServiceTests()
    {
        var databaseName = Guid.NewGuid().ToString();
        _provider = new ServiceCollection()
            .AddDbContext<OrbitDbContext>(options => options.UseInMemoryDatabase(databaseName))
            .AddSingleton(_mediator)
            .BuildServiceProvider();
        _scope = _provider.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        _service = new CalendarAutoSyncService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CalendarAutoSyncService>.Instance,
            new ConfigurationBuilder().Build(),
            new FakeTimeProvider());
        _mediator.Send(Arg.Any<RunCalendarAutoSyncCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _dispatchedUserIds.Add(call.Arg<RunCalendarAutoSyncCommand>().UserId);
                return Result.Success(new CalendarAutoSyncResult(0, 0, GoogleCalendarAutoSyncStatus.Idle));
            });
    }

    [Fact]
    public async Task RunAsync_EnabledConnectedUnexpiredProNeverSynced_DispatchesUser()
    {
        var user = CreateEligibleUser();

        await SeedAndRunAsync(user);

        _dispatchedUserIds.Should().Equal(user.Id);
    }

    [Fact]
    public async Task RunAsync_LifetimeProWithoutSubscription_DispatchesUser()
    {
        var user = User.Create("Lifetime", "lifetime@example.com").Value;
        user.GrantLifetimePro();
        user.SetGoogleTokens("access", "refresh");
        user.EnableCalendarAutoSync().IsSuccess.Should().BeTrue();
        user.Plan.Should().Be(UserPlan.Free);
        user.PlanExpiresAt.Should().BeNull();

        await SeedAndRunAsync(user);

        _dispatchedUserIds.Should().Equal(user.Id);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task RunAsync_ProviderCanceledWithBonus_UsesIndependentBonusExpiry(bool play, bool activeBonus)
    {
        var clock = new FakeTimeProvider();
        var user = User.Create("Bonus", "bonus@example.com", clock).Value;
        user.StartTrial(NowUtc.AddDays(-30));
        if (play)
            user.SetPlaySubscription("play-token", NowUtc.AddDays(-1));
        else
            user.SetStripeSubscription("subscription", NowUtc.AddDays(-1));
        user.GrantBonusProDays(7);
        if (play)
            user.CancelPlaySubscription(SubscriptionLapseReason.Expired);
        else
            user.CancelStripeSubscription(SubscriptionLapseReason.Expired);
        user.SetGoogleTokens("access", "refresh");
        user.EnableCalendarAutoSync().IsSuccess.Should().BeTrue();
        if (!activeBonus)
            _db.Entry(user).Property(u => u.BonusProExpiresAtUtc).CurrentValue = NowUtc;

        await SeedAndRunAsync(user);

        _dispatchedUserIds.Should().Equal(activeBonus ? new[] { user.Id } : []);
    }

    [Fact]
    public async Task RunAsync_AutoSyncDisabled_DoesNotDispatchUser()
    {
        var user = CreateEligibleUser();
        user.DisableCalendarAutoSync();

        await SeedAndRunAsync(user);

        _dispatchedUserIds.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_EnabledWithoutAccessToken_DoesNotDispatchUser()
    {
        var user = CreateEligibleUser();
        _db.Users.Add(user);
        _db.Entry(user).Property(u => u.GoogleAccessToken).CurrentValue = null;
        user.GoogleCalendarAutoSyncEnabled.Should().BeTrue();

        await SeedAndRunAsync();

        _dispatchedUserIds.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_FreePlan_DoesNotDispatchUser()
    {
        var user = CreateEligibleUser();
        _db.Users.Add(user);
        _db.Entry(user).Property(u => u.SubscriptionPlan).CurrentValue = UserPlan.Free;
        user.GoogleCalendarAutoSyncEnabled.Should().BeTrue();
        user.PlanExpiresAt.Should().BeAfter(NowUtc);

        await SeedAndRunAsync();

        _dispatchedUserIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task RunAsync_ProSubscriptionExpiredOrExpiringNow_DoesNotDispatchUser(int expiryHoursFromNow)
    {
        var user = CreateEligibleUser();
        user.SetStripeSubscription("subscription", NowUtc.AddHours(expiryHoursFromNow));

        await SeedAndRunAsync(user);

        _dispatchedUserIds.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ProWithoutExpiry_DoesNotDispatchUser()
    {
        var user = CreateEligibleUser();
        _db.Users.Add(user);
        _db.Entry(user).Property(u => u.SubscriptionExpiresAtUtc).CurrentValue = null;
        user.Plan.Should().Be(UserPlan.Pro);

        await SeedAndRunAsync();

        _dispatchedUserIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task RunAsync_LastSyncInsideOrAtDedupeWindow_DoesNotDispatchUser(int hoursSinceSync)
    {
        var user = CreateEligibleUser();
        user.MarkCalendarSyncSuccess(NowUtc.AddHours(-hoursSinceSync));

        await SeedAndRunAsync(user);

        _dispatchedUserIds.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_LastSyncOutsideDedupeWindow_DispatchesUser()
    {
        var user = CreateEligibleUser();
        user.MarkCalendarSyncSuccess(NowUtc.AddHours(-5));

        await SeedAndRunAsync(user);

        _dispatchedUserIds.Should().Equal(user.Id);
    }

    [Fact]
    public async Task RunAsync_SixtyEligibleUsers_DispatchesFiftyLeastRecentlySyncedInOrder()
    {
        var users = Enumerable.Range(0, 60).Select(index =>
        {
            var user = CreateEligibleUser();
            if (index != 0)
                user.MarkCalendarSyncSuccess(NowUtc.AddHours(-5 - index));
            return user;
        }).ToArray();
        var expectedUserIds = new[] { users[0].Id }
            .Concat(users.Skip(1).Reverse().Take(49).Select(user => user.Id));

        await SeedAndRunAsync(users);

        _dispatchedUserIds.Should().HaveCount(50).And.Equal(expectedUserIds);
    }

    [Fact]
    public async Task RunAsync_SecondUserThrows_DispatchesFirstAndThirdWithoutRethrowing()
    {
        var users = Enumerable.Range(0, 3).Select(index =>
        {
            var user = CreateEligibleUser();
            user.MarkCalendarSyncSuccess(NowUtc.AddHours(-7 + index));
            return user;
        }).ToArray();
        _mediator.Send(Arg.Is<RunCalendarAutoSyncCommand>(command => command.UserId == users[1].Id),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _dispatchedUserIds.Add(call.Arg<RunCalendarAutoSyncCommand>().UserId);
                return Task.FromException<Result<CalendarAutoSyncResult>>(
                    new InvalidOperationException("Sync failed"));
            });

        var run = () => SeedAndRunAsync(users);

        await run.Should().NotThrowAsync();
        _dispatchedUserIds.Should().Equal(users.Select(user => user.Id));
    }

    [Fact]
    public async Task RunAsync_NoEligibleUsers_CompletesWithoutDispatch()
    {
        var run = () => SeedAndRunAsync();

        await run.Should().NotThrowAsync();
        _dispatchedUserIds.Should().BeEmpty();
    }

    private static User CreateEligibleUser()
    {
        var user = User.Create("Calendar user", $"{Guid.NewGuid()}@example.com").Value;
        user.SetStripeSubscription("subscription", NowUtc.AddDays(30));
        user.SetGoogleTokens("access", "refresh");
        user.EnableCalendarAutoSync().IsSuccess.Should().BeTrue();
        return user;
    }

    private async Task SeedAndRunAsync(params User[] users)
    {
        _db.Users.AddRange(users);
        await _db.SaveChangesAsync();
        await _service.RunAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        _service.Dispose();
        _scope.Dispose();
        _provider.Dispose();
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(NowUtc);
    }
}
