using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Api.Seed;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class StagingSeedServiceTests
{
    private static readonly DateOnly Today = new(2026, 9, 27);

    [Fact]
    public async Task EmptyDatabaseGetsOwnerSampleData()
    {
        await using var db = CreateDatabase();

        await new StagingSeedService(db).SeedAsync("owner@example.com", Today);

        (await db.Users.CountAsync()).Should().Be(1);
        (await db.Habits.CountAsync()).Should().Be(12);
        (await db.Tags.CountAsync()).Should().Be(3);
        (await db.Goals.CountAsync()).Should().Be(2);
        (await db.HabitLogs.CountAsync()).Should().BeGreaterThan(30);
        (await db.Habits.SingleAsync(h => h.Title == "Stretch for five minutes")).ParentHabitId.Should().NotBeNull();
        (await db.Habits.SingleAsync(h => h.Title == "Morning routine")).ChecklistItems.Should().HaveCount(2);
        (await db.Goals.SingleAsync(g => g.Title == "Build a hydration streak")).CurrentValue.Should().BeGreaterThan(0);
        (await db.Habits.SingleAsync(h => h.Title == "Drink water")).CreatedAtUtc.Should().BeBefore(DateTime.UtcNow.AddDays(-28));
    }

    [Fact]
    public async Task SecondRunDoesNotDuplicateSamples()
    {
        await using var db = CreateDatabase();
        var seed = new StagingSeedService(db);
        await seed.SeedAsync("owner@example.com", Today);
        var before = await CountsAsync(db);

        await seed.SeedAsync("owner@example.com", Today);

        (await CountsAsync(db)).Should().Be(before);
    }

    [Theory]
    [InlineData("Production", "Host=staging.render.com;Database=orbit_staging;Username=orbit_staging;Password=x", "staging.render.com")]
    [InlineData("Staging", "Host=production.render.com;Database=orbit_production;Username=orbit_production;Password=x", "staging.render.com")]
    [InlineData("Staging", "Host=production.render.com;Database=orbit_staging;Username=orbit_staging;Password=x", "staging.render.com")]
    public void ProductionTargetIsRejectedBeforeOpeningDatabase(string environment, string connection, string expectedHost)
    {
        var action = () => StagingSeedCommand.ValidateTarget(environment, connection, expectedHost, "owner@example.com");

        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void PostgresUrlIsConvertedForNpgsql()
    {
        var result = StagingSeedCommand.NormalizeConnectionString("postgresql://orbit_staging:p%40ss@staging.render.com/orbit_staging");

        StagingSeedCommand.ValidateTarget("Staging", result, "staging.render.com", "owner@example.com");
    }

    private static OrbitDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrbitDbContext(options);
    }

    private static async Task<(int Users, int Habits, int Tags, int Goals, int Logs)> CountsAsync(OrbitDbContext db) =>
        (await db.Users.CountAsync(), await db.Habits.CountAsync(), await db.Tags.CountAsync(),
            await db.Goals.CountAsync(), await db.HabitLogs.CountAsync());
}
