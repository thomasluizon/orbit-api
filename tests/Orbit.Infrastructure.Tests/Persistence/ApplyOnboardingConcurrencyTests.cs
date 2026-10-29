using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

/// <summary>
/// Verifies that <see cref="ApplyOnboardingCommand"/> applies exactly once when the first save hits a
/// simulated stale-token conflict and the concurrency-retry pipeline re-runs the whole handler. The
/// in-memory provider does not enforce xmin, so the conflict is injected with a save interceptor.
/// </summary>
public class ApplyOnboardingConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_TrackedUserChangedBeforeLock_ReloadsPreferencesAndCompletion(bool alreadyApplied)
    {
        await using var database = new UserRowConcurrencyHarness(user =>
        {
            user.SetTimeZone("America/Sao_Paulo");
            if (alreadyApplied) user.CompleteOnboarding();
        });
        var user = User.Create("Onboarding user", "onboarding@example.com").Value;
        await database.SeedAsync(user);
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(AppConfigKeys.FreeMaxHabits, AppConstants.DefaultFreeMaxHabits, Arg.Any<CancellationToken>())
            .Returns(AppConstants.DefaultFreeMaxHabits);
        var handler = new ApplyOnboardingCommandHandler(new ApplyOnboardingRepositories(
            new GenericRepository<User>(database.Context), new GenericRepository<Habit>(database.Context),
            new GenericRepository<Goal>(database.Context)), StubToday(new DateOnly(2030, 4, 10)), config,
            database.Work, new MemoryCache(new MemoryCacheOptions()));
        var command = new ApplyOnboardingCommand(user.Id,
            [new ApplyHabitInput("Drink water", null, null, FrequencyUnit.Day, 1)], null, null, null);
        var retry = new ConcurrencyRetryBehavior<ApplyOnboardingCommand, Result<ApplyOnboardingResponse>>(database.Work);

        var result = await retry.Handle(command, ct => handler.Handle(command, ct), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Applied.Should().Be(!alreadyApplied);
        database.SaveAttempts.Should().Be(alreadyApplied ? 0 : 1);
        database.ConcurrentWrites.Should().Be(1);
        await using var verify = database.CreateContext();
        var saved = await verify.Users.SingleAsync();
        saved.HasCompletedOnboarding.Should().BeTrue();
        saved.TimeZone.Should().Be("America/Sao_Paulo");
        (await verify.Habits.CountAsync()).Should().Be(alreadyApplied ? 0 : 1);
    }

    [Fact]
    public async Task Apply_ConflictOnFirstSave_RetriesAndAppliesExactlyOnce()
    {
        var dbName = $"ApplyOnboardingConcurrency_{Guid.NewGuid()}";
        Guid userId;

        await using (var seed = CreateContext(dbName))
        {
            var user = User.Create("Tester", $"{Guid.NewGuid():N}@example.com").Value;
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
            userId = user.Id;
        }

        var interceptor = new ConflictOnceInterceptor();
        await using var context = CreateContext(dbName, interceptor);
        var unitOfWork = new UnitOfWork(context, new DatabaseConnectionSettings());
        var appConfig = Substitute.For<IAppConfigService>();
        appConfig.GetAsync(
                AppConfigKeys.FreeMaxHabits,
                AppConstants.DefaultFreeMaxHabits,
                Arg.Any<CancellationToken>())
            .Returns(AppConstants.DefaultFreeMaxHabits);
        var handler = new ApplyOnboardingCommandHandler(
            new ApplyOnboardingRepositories(
                new GenericRepository<User>(context),
                new GenericRepository<Habit>(context),
                new GenericRepository<Goal>(context)),
            StubToday(new DateOnly(2026, 7, 5)),
            appConfig,
            unitOfWork,
            new MemoryCache(new MemoryCacheOptions()));

        var command = new ApplyOnboardingCommand(
            userId,
            [new ApplyHabitInput("Drink water", null, null, FrequencyUnit.Day, 1)],
            null, null, null);

        var behavior = new ConcurrencyRetryBehavior<ApplyOnboardingCommand, Result<ApplyOnboardingResponse>>(unitOfWork);
        var result = await behavior.Handle(command, ct => handler.Handle(command, ct), CancellationToken.None);

        interceptor.SaveAttempts.Should().Be(2);
        result.IsSuccess.Should().BeTrue();
        result.Value.Applied.Should().BeTrue();
        result.Value.CreatedHabitCount.Should().Be(1);

        await using var verify = CreateContext(dbName);
        verify.Users.Single(u => u.Id == userId).HasCompletedOnboarding.Should().BeTrue();
        verify.Habits.Count(h => h.UserId == userId).Should().Be(1);
    }

    private static OrbitDbContext CreateContext(string dbName, ISaveChangesInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<OrbitDbContext>().UseInMemoryDatabase(dbName);
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);
        return new OrbitDbContext(builder.Options);
    }

    private static IUserDateService StubToday(DateOnly today)
    {
        var service = Substitute.For<IUserDateService>();
        service.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(today);
        return service;
    }

    private sealed class ConflictOnceInterceptor : SaveChangesInterceptor
    {
        public int SaveAttempts { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            if (SaveAttempts == 1)
                throw new DbUpdateConcurrencyException("simulated stale token");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
