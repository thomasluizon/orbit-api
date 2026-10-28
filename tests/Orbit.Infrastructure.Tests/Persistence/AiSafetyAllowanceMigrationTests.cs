using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Application.Common;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class AiSafetyAllowanceMigrationTests
{
    [Fact]
    public void Migration_UpdatesExistingSafetyRowsAndMatchesRuntimeFallbacks()
    {
        var migration = new FitAiSafetyBudgetsToAllowances();
        UpdatedValues(migration.UpOperations).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            [AppConfigKeys.FreeAiCallsPerDay] = AppConfigKeys.DefaultFreeAiCallsPerDay.ToString(),
            [AppConfigKeys.FreeAiMicroDollarsPerDay] = AppConfigKeys.DefaultFreeAiMicroDollarsPerDay.ToString(),
            [AppConfigKeys.ProAiCallsPerWeek] = AppConfigKeys.DefaultProAiCallsPerWeek.ToString(),
            [AppConfigKeys.ProAiMicroDollarsPerWeek] = AppConfigKeys.DefaultProAiMicroDollarsPerWeek.ToString()
        });
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Fact]
    public void Rollback_RestoresPreviousSafetyCaps()
    {
        var migration = new FitAiSafetyBudgetsToAllowances();
        UpdatedValues(migration.DownOperations).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            [AppConfigKeys.FreeAiCallsPerDay] = "45",
            [AppConfigKeys.FreeAiMicroDollarsPerDay] = "20000",
            [AppConfigKeys.ProAiCallsPerWeek] = "2250",
            [AppConfigKeys.ProAiMicroDollarsPerWeek] = "1750000"
        });
    }

    private static Dictionary<string, string> UpdatedValues(IReadOnlyList<MigrationOperation> operations)
    {
        operations.Should().HaveCount(4);
        return operations.Select(operation =>
        {
            var update = operation.Should().BeOfType<UpdateDataOperation>().Subject;
            update.Table.Should().Be("AppConfigs");
            update.KeyColumns.Should().Equal("Key");
            update.Columns.Should().Equal("Value");
            return new KeyValuePair<string, string>(update.KeyValues[0, 0].Should().BeOfType<string>().Subject,
                update.Values[0, 0].Should().BeOfType<string>().Subject);
        }).ToDictionary();
    }
}
