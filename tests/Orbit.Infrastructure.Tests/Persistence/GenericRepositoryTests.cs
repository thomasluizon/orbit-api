using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

/// <summary>
/// Exercises the tracking contract of <see cref="GenericRepository{T}"/> against the real
/// <see cref="OrbitDbContext"/>: the tracked read variants return change-tracked entities whose
/// in-place mutations persist on the next SaveChanges without an explicit Update, the AsNoTracking
/// variants return detached snapshots whose mutations are dropped, and the soft-delete query filter
/// is applied to the tracked reads while the IgnoringFilters variants bypass it.
/// </summary>
public class GenericRepositoryTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task FindTrackedAsync_ReturnsTrackedEntities_MutationPersistsWithoutExplicitUpdate()
    {
        var databaseName = NewDatabaseName();
        Guid goalId;
        await using (var seed = CreateContext(databaseName))
        {
            var seeded = MakeGoal("Squat");
            seed.Goals.Add(seeded);
            await seed.SaveChangesAsync();
            goalId = seeded.Id;
        }

        await using var context = CreateContext(databaseName);
        var repository = new GenericRepository<Goal>(context);

        var goal = (await repository.FindTrackedAsync(g => g.Id == goalId)).Single();
        context.Entry(goal).State.Should().Be(EntityState.Unchanged);

        goal.UpdatePosition(7);
        context.Entry(goal).State.Should().Be(EntityState.Modified);
        await context.SaveChangesAsync();

        await using var verify = CreateContext(databaseName);
        verify.Goals.Single(g => g.Id == goalId).Position.Should().Be(7);
    }

    [Fact]
    public async Task FindAsync_ReturnsUntrackedEntities_MutationIsNotPersisted()
    {
        var databaseName = NewDatabaseName();
        Guid goalId;
        await using (var seed = CreateContext(databaseName))
        {
            var seeded = MakeGoal("Squat");
            seeded.UpdatePosition(3);
            seed.Goals.Add(seeded);
            await seed.SaveChangesAsync();
            goalId = seeded.Id;
        }

        await using var context = CreateContext(databaseName);
        var repository = new GenericRepository<Goal>(context);

        var untracked = (await repository.FindAsync(g => g.Id == goalId)).Single();
        context.Entry(untracked).State.Should().Be(EntityState.Detached);

        untracked.UpdatePosition(99);
        await context.SaveChangesAsync();

        await using var verify = CreateContext(databaseName);
        verify.Goals.Single(g => g.Id == goalId).Position.Should().Be(3);
    }

    [Fact]
    public async Task FindOneTrackedAsync_WithIncludes_ReturnsTrackedRootWithLoadedNavigations()
    {
        var databaseName = NewDatabaseName();
        Guid goalId;
        await using (var seed = CreateContext(databaseName))
        {
            var seeded = MakeGoal("With logs");
            seed.Goals.Add(seeded);
            seed.GoalProgressLogs.Add(GoalProgressLog.Create(seeded.Id, 0, 10));
            seed.GoalProgressLogs.Add(GoalProgressLog.Create(seeded.Id, 10, 25));
            await seed.SaveChangesAsync();
            goalId = seeded.Id;
        }

        await using var context = CreateContext(databaseName);
        var repository = new GenericRepository<Goal>(context);

        var goal = await repository.FindOneTrackedAsync(
            g => g.Id == goalId, includes: query => query.Include(g => g.ProgressLogs));

        goal.Should().NotBeNull();
        goal!.ProgressLogs.Should().HaveCount(2);
        context.Entry(goal).State.Should().Be(EntityState.Unchanged);
    }

    [Fact]
    public async Task TrackedReads_ExcludeSoftDeletedRowsViaQueryFilter()
    {
        var databaseName = NewDatabaseName();
        Guid liveId, deletedId;
        await using (var seed = CreateContext(databaseName))
        {
            var live = MakeGoal("Live");
            var deleted = MakeGoal("Deleted");
            deleted.SoftDelete();
            seed.Goals.AddRange(live, deleted);
            await seed.SaveChangesAsync();
            liveId = live.Id;
            deletedId = deleted.Id;
        }

        await using var context = CreateContext(databaseName);
        var repository = new GenericRepository<Goal>(context);

        var many = await repository.FindTrackedAsync(g => g.UserId == UserId);
        var deletedById = await repository.FindOneTrackedAsync(g => g.Id == deletedId);

        many.Select(g => g.Id).Should().Equal(liveId);
        deletedById.Should().BeNull();
    }

    [Fact]
    public async Task IgnoringFiltersTrackedReads_ReturnSoftDeletedRowTracked_RestorePersistsAndUnhides()
    {
        var databaseName = NewDatabaseName();
        Guid deletedId;
        await using (var seed = CreateContext(databaseName))
        {
            var deleted = MakeGoal("Deleted");
            deleted.SoftDelete();
            seed.Goals.Add(deleted);
            await seed.SaveChangesAsync();
            deletedId = deleted.Id;
        }

        await using var context = CreateContext(databaseName);
        var repository = new GenericRepository<Goal>(context);

        var many = await repository.FindTrackedIgnoringFiltersAsync(g => g.Id == deletedId);
        var one = await repository.FindOneTrackedIgnoringFiltersAsync(g => g.Id == deletedId);

        many.Should().ContainSingle();
        one.Should().NotBeNull();
        context.Entry(one!).State.Should().Be(EntityState.Unchanged);

        one!.Restore();
        await context.SaveChangesAsync();

        await using var verify = CreateContext(databaseName);
        var verifyRepository = new GenericRepository<Goal>(verify);
        (await verifyRepository.FindOneTrackedAsync(g => g.Id == deletedId)).Should().NotBeNull();
    }

    private static Goal MakeGoal(string title) =>
        Goal.Create(new Goal.CreateGoalParams(UserId, title, 100, "units")).Value;

    private static string NewDatabaseName() => $"GenericRepositoryTests_{Guid.NewGuid()}";

    private static OrbitDbContext CreateContext(string databaseName) =>
        new(new DbContextOptionsBuilder<OrbitDbContext>().UseInMemoryDatabase(databaseName).Options);
}
