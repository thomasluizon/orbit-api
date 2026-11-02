using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbit.Application.Common;
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

    private static readonly Dictionary<string, string> ExpectedPublicEndpointBudgets = new()
    {
        [AppConfigKeys.SendCodeEmailsPerIpPerDay] = "100",
        [AppConfigKeys.SendCodeEmailsPerDay] = "10000",
        [AppConfigKeys.OnboardingAttestationCallsPerHour] = "1000",
        [AppConfigKeys.OnboardingAttestationCallsPerDay] = "10000",
        [AppConfigKeys.OnboardingEnrollAttemptsPerInstallPerHour] = "3"
    };

    [Fact]
    public async Task Model_SeedsOnboardingAndPublicEmailBudgets()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var values = await factory.Context.AppConfigs
            .Where(config => config.Key.StartsWith("Onboarding") || config.Key.StartsWith("SendCodeEmails"))
            .ToDictionaryAsync(config => config.Key, config => config.Value);

        values.Should().BeEquivalentTo(ExpectedBudgets.Concat(ExpectedPublicEndpointBudgets)
            .ToDictionary(pair => pair.Key, pair => pair.Value));
        long.Parse(values[OnboardingAccessService.DailyTokenLimit]).Should().Be(
            long.Parse(values[OnboardingAccessService.DailyCallLimit]) * OnboardingAccessService.ReservedTokensPerCall);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migration_SeedsBudgetsAndRollbackPreservesOtherConfiguration(bool publicEndpoints)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        var expected = publicEndpoints ? ExpectedPublicEndpointBudgets : ExpectedBudgets;
        var keys = expected.Keys.ToArray();
        await context.AppConfigs.Where(config => keys.Contains(config.Key)).ExecuteDeleteAsync();
        var original = await context.AppConfigs.ToDictionaryAsync(config => config.Key, config => config.Value);
        Migration migration = publicEndpoints ? new SeedPublicEndpointAbuseQuotas() : new SeedOnboardingBudgets();
        migration.ActiveProvider = "Microsoft.EntityFrameworkCore.Sqlite";
        var model = context.GetService<IDesignTimeModel>().Model;
        var generator = context.GetService<IMigrationsSqlGenerator>();

        foreach (var command in generator.Generate(migration.UpOperations, model))
            await context.Database.ExecuteSqlRawAsync(command.CommandText);

        var seeded = await context.AppConfigs.Where(config => keys.Contains(config.Key))
            .ToDictionaryAsync(config => config.Key, config => config.Value);
        seeded.Should().BeEquivalentTo(expected);

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
