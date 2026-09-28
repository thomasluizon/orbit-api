using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
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
        var task = await db.Habits.SingleAsync(h => h.Title == "Submit a document");
        task.DueDate.Should().Be(Today);
        task.IsCompleted.Should().BeFalse();
        (await db.Goals.SingleAsync(g => g.Title == "Build a hydration streak")).CurrentValue.Should().BeGreaterThan(0);
        (await db.Habits.SingleAsync(h => h.Title == "Drink water")).CreatedAtUtc.Should().BeBefore(DateTime.UtcNow.AddDays(-28));
    }

    [Fact]
    public async Task SecondRunDoesNotDuplicateSamples()
    {
        var databaseName = Guid.NewGuid().ToString();
        var root = new InMemoryDatabaseRoot();
        (int Users, int Habits, int Tags, int Goals, int Logs) before;
        await using (var firstDb = CreateDatabase(databaseName, root))
        {
            await new StagingSeedService(firstDb).SeedAsync("owner@example.com", Today);
            before = await CountsAsync(firstDb);
        }

        await using var secondDb = CreateDatabase(databaseName, root);
        await new StagingSeedService(secondDb).SeedAsync("owner@example.com", Today);

        (await CountsAsync(secondDb)).Should().Be(before);
    }

    [Theory]
    [InlineData("Production", "Host=staging.render.com;Database=orbit_staging;Username=orbit_staging;Password=x", "staging.render.com")]
    [InlineData("Staging", "Host=production.render.com;Database=orbit_production_9g8l;Username=orbit_production;Password=x", "staging.render.com")]
    [InlineData("Staging", "Host=production.render.com;Database=orbit_staging;Username=orbit_staging;Password=x", "staging.render.com")]
    [InlineData("Staging", "Host=staging.render.com;Database=orbit_production_9g8l;Username=orbit_staging;Password=x", "staging.render.com")]
    [InlineData("Staging", "Host=staging.render.com;Database=other_staging_jo8c;Username=orbit_staging;Password=x", "staging.render.com")]
    [InlineData("Staging", "Host=staging.render.com;Database=orbit_staging_jo8c_extra;Username=orbit_staging;Password=x", "staging.render.com")]
    [InlineData("Staging", "Host=staging.render.com;Database=orbit_staging_jo8c;Username=orbit_production;Password=x", "staging.render.com")]
    public void ProductionTargetIsRejectedBeforeOpeningDatabase(string environment, string connection, string expectedHost)
    {
        var action = () => StagingSeedCommand.ValidateTarget(environment, connection, expectedHost, "owner@example.com");

        action.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("orbit_staging")]
    [InlineData("orbit_staging_jo8c")]
    [InlineData("orbit_staging_abc123")]
    public void StagingTargetIsAcceptedBeforeOpeningDatabase(string database)
    {
        var connection = $"Host=staging.render.com;Database={database};Username=orbit_staging;Password=x";

        var action = () => StagingSeedCommand.ValidateTarget("Staging", connection, "staging.render.com", "owner@example.com");

        action.Should().NotThrow();
    }

    [Theory]
    [InlineData("Production", "staging.render.com", "orbit_staging")]
    [InlineData("Staging", "production.render.com", "orbit_staging")]
    [InlineData("Staging", "staging.render.com", "orbit_production_9g8l")]
    public async Task MigrationRejectsUnsafeTargetBeforeOpeningDatabase(string environment, string host, string database)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Seed:DatabaseUrl"] = $"Host={host};Database={database};Username=orbit_staging;Password=x",
            ["Seed:ExpectedHost"] = "staging.render.com",
            ["Seed:OwnerEmail"] = "owner@example.com"
        }).Build();

        var action = () => StagingSeedCommand.MigrateAsync(configuration, environment);

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void PostgresUrlIsConvertedForNpgsql()
    {
        var encodedPassword = Uri.EscapeDataString(string.Join(string.Empty, "p", "@", "ss"));
        var url = $"postgresql://orbit_staging:{encodedPassword}@staging.render.com/orbit_staging_jo8c";
        var result = StagingSeedCommand.NormalizeConnectionString(url);

        StagingSeedCommand.ValidateTarget("Staging", result, "staging.render.com", "owner@example.com");
        new Npgsql.NpgsqlConnectionStringBuilder(result).Database.Should().Be("orbit_staging_jo8c");
        new Npgsql.NpgsqlConnectionStringBuilder(result).Password.Should().Be("p@ss");
    }

    private static OrbitDbContext CreateDatabase(string? name = null, InMemoryDatabaseRoot? root = null)
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString(), root)
            .Options;
        return new OrbitDbContext(options);
    }

    private static async Task<(int Users, int Habits, int Tags, int Goals, int Logs)> CountsAsync(OrbitDbContext db) =>
        (await db.Users.CountAsync(), await db.Habits.CountAsync(), await db.Tags.CountAsync(),
            await db.Goals.CountAsync(), await db.HabitLogs.CountAsync());
}
