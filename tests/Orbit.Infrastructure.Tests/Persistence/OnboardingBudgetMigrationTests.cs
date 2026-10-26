using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public sealed class OnboardingBudgetMigrationTests
{
    private static readonly Dictionary<string, string> ExpectedBudgets = new()
    {
        [OnboardingAccessService.IpHourlyLimit] = "10",
        [OnboardingAccessService.InstallHourlyLimit] = "10",
        [OnboardingAccessService.DailyCallLimit] = "1000",
        [OnboardingAccessService.DailyTokenLimit] = "16384000"
    };

    [Fact]
    public async Task Model_SeedsTheFourOnboardingBudgets()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var values = await factory.Context.AppConfigs
            .Where(config => config.Key.StartsWith("Onboarding"))
            .ToDictionaryAsync(config => config.Key, config => config.Value);

        values.Should().BeEquivalentTo(ExpectedBudgets);
        long.Parse(values[OnboardingAccessService.DailyTokenLimit]).Should().Be(
            long.Parse(values[OnboardingAccessService.DailyCallLimit]) * OnboardingAccessService.ReservedTokensPerCall);
    }

    [Fact]
    public async Task Migration_SeedsBudgetsAndRollbackPreservesOtherConfiguration()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        await context.AppConfigs.Where(config => config.Key.StartsWith("Onboarding")).ExecuteDeleteAsync();
        var original = await context.AppConfigs.ToDictionaryAsync(config => config.Key, config => config.Value);
        var migration = new SeedOnboardingBudgets { ActiveProvider = "Microsoft.EntityFrameworkCore.Sqlite" };
        var model = context.GetService<IDesignTimeModel>().Model;
        var generator = context.GetService<IMigrationsSqlGenerator>();

        foreach (var command in generator.Generate(migration.UpOperations, model))
            await context.Database.ExecuteSqlRawAsync(command.CommandText);

        var seeded = await context.AppConfigs.Where(config => config.Key.StartsWith("Onboarding"))
            .ToDictionaryAsync(config => config.Key, config => config.Value);
        seeded.Should().BeEquivalentTo(ExpectedBudgets);

        foreach (var command in generator.Generate(migration.DownOperations, model))
            await context.Database.ExecuteSqlRawAsync(command.CommandText);

        (await context.AppConfigs.ToDictionaryAsync(config => config.Key, config => config.Value))
            .Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Model_HasNoChangesMissingFromTheMigrationSnapshot()
    {
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);

        context.Database.HasPendingModelChanges().Should().BeFalse();
    }
}
