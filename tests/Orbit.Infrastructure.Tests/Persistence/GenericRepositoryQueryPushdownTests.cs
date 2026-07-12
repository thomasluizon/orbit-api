using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

/// <summary>
/// Exercises the DB-side filter, ordering, pagination, and aggregation the read queries push down
/// through <see cref="GenericRepository{T}"/>, against the real <see cref="OrbitDbContext"/> so the
/// global query filters and range boundaries are decided by EF, not a mock.
/// </summary>
public class GenericRepositoryQueryPushdownTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    [Fact]
    public async Task FindPagedAsync_ReturnsOrderedPageSliceWithFullMatchCount()
    {
        await using var context = CreateContext();
        foreach (var position in new[] { 3, 0, 4, 1, 2 })
            context.Goals.Add(MakeGoal($"Goal {position}", position));
        await context.SaveChangesAsync();
        var repo = new GenericRepository<Goal>(context);

        var (page1, total1) = await repo.FindPagedAsync(
            g => g.UserId == UserId, Order, page: 1, pageSize: 2);
        var (page2, _) = await repo.FindPagedAsync(
            g => g.UserId == UserId, Order, page: 2, pageSize: 2);
        var (lastPage, _) = await repo.FindPagedAsync(
            g => g.UserId == UserId, Order, page: 3, pageSize: 2);
        var (beyond, totalBeyond) = await repo.FindPagedAsync(
            g => g.UserId == UserId, Order, page: 4, pageSize: 2);

        total1.Should().Be(5);
        page1.Select(g => g.Position).Should().Equal(0, 1);
        page2.Select(g => g.Position).Should().Equal(2, 3);
        lastPage.Select(g => g.Position).Should().Equal(4);
        beyond.Should().BeEmpty();
        totalBeyond.Should().Be(5);
    }

    [Fact]
    public async Task FindPagedAsync_ExcludesSoftDeletedAndOtherUsersFromPageAndCount()
    {
        await using var context = CreateContext();
        context.Goals.Add(MakeGoal("Live A", 0));
        context.Goals.Add(MakeGoal("Live B", 1));
        var deleted = MakeGoal("Deleted", 2);
        deleted.SoftDelete();
        context.Goals.Add(deleted);
        context.Goals.Add(MakeGoal("Other user", 0, OtherUserId));
        await context.SaveChangesAsync();
        var repo = new GenericRepository<Goal>(context);

        var (items, total) = await repo.FindPagedAsync(
            g => g.UserId == UserId, Order, page: 1, pageSize: 50);

        total.Should().Be(2);
        items.Select(g => g.Title).Should().BeEquivalentTo("Live A", "Live B");
    }

    [Fact]
    public async Task FindPagedAsync_AppliesIncludesToLoadNavigations()
    {
        await using var context = CreateContext();
        var goal = MakeGoal("With logs", 0);
        context.Goals.Add(goal);
        context.GoalProgressLogs.Add(GoalProgressLog.Create(goal.Id, 0, 10));
        context.GoalProgressLogs.Add(GoalProgressLog.Create(goal.Id, 10, 25));
        await context.SaveChangesAsync();
        var repo = new GenericRepository<Goal>(context);

        var (withInclude, _) = await repo.FindPagedAsync(
            g => g.UserId == UserId, Order, page: 1, pageSize: 50,
            includes: q => q.Include(g => g.ProgressLogs));
        var (withoutInclude, _) = await repo.FindPagedAsync(
            g => g.UserId == UserId, Order, page: 1, pageSize: 50);

        withInclude[0].ProgressLogs.Should().HaveCount(2);
        withoutInclude[0].ProgressLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task SumAsync_AggregatesMatchingRowsExcludingOtherUsers()
    {
        await using var context = CreateContext();
        context.XpAwardLogs.Add(Xp(10, Today));
        context.XpAwardLogs.Add(Xp(20, Today.AddDays(-1)));
        context.XpAwardLogs.Add(Xp(100, Today, OtherUserId));
        await context.SaveChangesAsync();
        var repo = new GenericRepository<XpAwardLog>(context);

        var sum = await repo.SumAsync(x => x.UserId == UserId, x => x.Amount);

        sum.Should().Be(30);
    }

    [Fact]
    public async Task SumAsync_HonorsExclusiveUpperBoundOnTimestamp()
    {
        await using var context = CreateContext();
        var fromUtc = Today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        context.XpAwardLogs.Add(XpAt(5, fromUtc.AddTicks(-1)));
        context.XpAwardLogs.Add(XpAt(7, fromUtc));
        await context.SaveChangesAsync();
        var repo = new GenericRepository<XpAwardLog>(context);

        var baseline = await repo.SumAsync(
            x => x.UserId == UserId && x.AwardedAtUtc < fromUtc, x => x.Amount);

        baseline.Should().Be(5);
    }

    [Fact]
    public async Task SumAsync_NoMatches_ReturnsZero()
    {
        await using var context = CreateContext();
        var repo = new GenericRepository<XpAwardLog>(context);

        var sum = await repo.SumAsync(x => x.UserId == UserId, x => x.Amount);

        sum.Should().Be(0);
    }

    private static IOrderedQueryable<Goal> Order(IQueryable<Goal> query) =>
        query.OrderBy(g => g.Position).ThenBy(g => g.CreatedAtUtc);

    private static Goal MakeGoal(string title, int position, Guid? userId = null)
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(userId ?? UserId, title, 100, "units")).Value;
        goal.UpdatePosition(position);
        return goal;
    }

    private static XpAwardLog Xp(int amount, DateOnly date, Guid? userId = null) =>
        XpAt(amount, date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), userId);

    private static XpAwardLog XpAt(int amount, DateTime awardedAtUtc, Guid? userId = null) =>
        XpAwardLog.Create(userId ?? UserId, amount, XpAwardSource.HabitLog, null, awardedAtUtc);

    private static OrbitDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"GenericRepositoryQueryPushdownTests_{Guid.NewGuid()}")
            .Options;
        return new OrbitDbContext(options);
    }
}
