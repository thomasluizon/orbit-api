using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class DailyAiDollarBudgetMigrationTests
{
    [Fact]
    public void DollarExpansion_PreservesLegacyWritersAndMatchesCurrentModel()
    {
        var migration = new PriceDailyAiBudgetsInUsd { ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL" };
        migration.UpOperations.OfType<DropColumnOperation>().Should().BeEmpty();
        migration.UpOperations.OfType<RenameColumnOperation>().Should().BeEmpty();
        var column = migration.UpOperations.OfType<AddColumnOperation>().Should().ContainSingle().Subject;
        column.Table.Should().Be("AiBudgetCalls");
        column.Name.Should().Be("ChargedMicroDollars");
        column.DefaultValue.Should().Be(0L);
        migration.UpOperations.OfType<DeleteDataOperation>().Select(operation => operation.KeyValues[0, 0])
            .Should().BeEquivalentTo(new object[] { "FreeAiTokensPerDay", "ProAiTokensPerDay" });
        var seeds = migration.UpOperations.OfType<InsertDataOperation>().Single();
        seeds.Values[0, 0].Should().Be("FreeAiMicroDollarsPerDay");
        seeds.Values[0, 2].Should().Be("20000");
        seeds.Values[1, 0].Should().Be("ProAiMicroDollarsPerDay");
        seeds.Values[1, 2].Should().Be("120000");

        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var target = context.GetService<IModelRuntimeInitializer>().Initialize(migration.TargetModel, designTime: true);
        var current = context.GetService<IDesignTimeModel>().Model;
        context.GetService<IMigrationsModelDiffer>().GetDifferences(target.GetRelationalModel(), current.GetRelationalModel())
            .Should().BeEmpty();
        var entity = current.FindEntityType(typeof(AiBudgetCall))!;
        entity.FindProperty("ChargedTokens").Should().NotBeNull();
        entity.FindProperty(nameof(AiBudgetCall.ChargedMicroDollars)).Should().NotBeNull();
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }
}
