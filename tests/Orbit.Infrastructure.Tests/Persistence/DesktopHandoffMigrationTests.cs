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

public class DesktopHandoffMigrationTests
{
    [Fact]
    public void Migration_FollowsHandleUnmapping()
    {
        using var context = CreateContext();
        var migrations = context.GetService<IMigrationsAssembly>().Migrations;
        var unmap = migrations.Single(pair => pair.Value.AsType() == typeof(UnmapUserHandle)).Key;
        var handoff = migrations.Single(pair => pair.Value.AsType() == typeof(AddDesktopHandoffs)).Key;

        migrations.Keys.Should().ContainInOrder(unmap, handoff);
    }

    [Fact]
    public void TargetModel_PreservesHandleUnmappingAndMatchesCurrentModel()
    {
        using var context = CreateContext();
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var target = initializer.Initialize(new AddDesktopHandoffs().TargetModel, designTime: true);
        var current = context.GetService<IDesignTimeModel>().Model;

        target.FindEntityType(typeof(User).FullName!)!.FindProperty("Handle").Should().BeNull();
        ChatTurnMigrationTests.AssertExpansion(context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(target.GetRelationalModel(), current.GetRelationalModel()));
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Fact]
    public void Up_AddsOnlyHandoffTableAndIndexesAndMatchesModelTransition()
    {
        var migration = new AddDesktopHandoffs();
        var operations = migration.UpOperations;
        operations.Should().HaveCount(4).And.OnlyContain(operation =>
            operation is CreateTableOperation || operation is CreateIndexOperation);
        operations.OfType<CreateTableOperation>().Should().ContainSingle().Which.Name.Should().Be("DesktopHandoffs");
        operations.OfType<CreateIndexOperation>().Should().HaveCount(3)
            .And.OnlyContain(index => index.Table == "DesktopHandoffs");

        using var context = CreateContext();
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var previous = initializer.Initialize(new UnmapUserHandle().TargetModel, designTime: true);
        var target = initializer.Initialize(migration.TargetModel, designTime: true);
        var differences = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(previous.GetRelationalModel(), target.GetRelationalModel());

        differences.Should().BeEquivalentTo(operations, options => options.PreferringRuntimeMemberTypes());
    }

    private static OrbitDbContext CreateContext() => new(new DbContextOptionsBuilder<OrbitDbContext>()
        .UseNpgsql("Host=unused;Database=unused").Options);
}
