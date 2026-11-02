using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;
using Orbit.Application.Marketing.Jobs;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class UnmapMarketingBroadcastDeliveriesMigrationTests
{
    [Fact]
    public void UpAndDown_PreserveTheTableUntilALaterDeploymentBarrier()
    {
        var migration = new UnmapMarketingBroadcastDeliveries();

        migration.UpOperations.Should().BeEmpty();
        migration.DownOperations.Should().BeEmpty();
        RollingMigrationGate.Check("unmap", migration.UpOperations).Should().BeEmpty();
        RollingMigrationGate.Check("contract", [new DropTableOperation { Name = "MarketingBroadcastDeliveries" }])
            .Should().ContainSingle().Which.Should().Contain("deployment barrier");
    }

    [Fact]
    public void TargetModel_UnmapsOnlyTheSupersededTable()
    {
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var previous = initializer.Initialize(new CompleteDurableAuthAndDeliveryState().TargetModel, designTime: true);
        var target = initializer.Initialize(new UnmapMarketingBroadcastDeliveries().TargetModel, designTime: true);
        var changes = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(previous.GetRelationalModel(), target.GetRelationalModel());

        changes.Should().ContainSingle().Which.Should().BeOfType<DropTableOperation>()
            .Which.Name.Should().Be("MarketingBroadcastDeliveries");
    }

    [PostgresFact]
    public Task Migrate_UnmapsTheTableWithoutDroppingItsSchema()
        => PostgresTestDatabase.WithDatabase(async context =>
        {
            (await context.Database.SqlQueryRaw<int>("""
                SELECT count(*)::int AS "Value" FROM information_schema.tables
                WHERE table_schema = 'public' AND table_name = 'MarketingBroadcastDeliveries'
                """).SingleAsync()).Should().Be(1);
            context.Model.GetEntityTypes().Select(entity => entity.GetTableName())
                .Should().NotContain("MarketingBroadcastDeliveries");
            context.Database.HasPendingModelChanges().Should().BeFalse();
        });

    [Fact]
    public void CurrentModel_UsesOnlyRecipientDeliveries()
    {
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);

        context.Model.GetEntityTypes().Select(entity => entity.GetTableName())
            .Should().Contain("MarketingDeliveries").And.NotContain("MarketingBroadcastDeliveries");
        typeof(OrbitDbContext).GetProperty("MarketingBroadcastDeliveries").Should().BeNull();
        typeof(MarketingDelivery).Assembly.GetType("Orbit.Domain.Entities.MarketingBroadcastDelivery").Should().BeNull();
        typeof(SendMarketingEmailJob).Assembly.GetType("Orbit.Application.Marketing.Jobs.SendMarketingBroadcastPageJob").Should().BeNull();
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }
}
