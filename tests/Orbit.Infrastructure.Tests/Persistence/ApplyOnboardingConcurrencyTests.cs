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
        var handler = new ApplyOnboardingCommandHandler(
            new GenericRepository<User>(context),
            new GenericRepository<Habit>(context),
            new GenericRepository<Goal>(context),
            Substitute.For<IPayGateService>(),
            StubToday(new DateOnly(2026, 7, 5)),
            Substitute.For<IAppConfigService>(),
            unitOfWork,
            new MemoryCache(new MemoryCacheOptions()));

        var command = new ApplyOnboardingCommand(
            userId,
            [new ApplyHabitInput("Drink water", null, null, FrequencyUnit.Day, 1)],
            null, null, null, null);

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
