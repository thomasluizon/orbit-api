using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class FeatureFlagAccountsMigrationTests
{
    [Fact]
    public void TargetModel_PreservesRedesign()
    {
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var target = initializer.Initialize(new AddFeatureFlagAccounts().TargetModel, designTime: true);
        var redesign = initializer.Initialize(new AddHabitSkipUndo().TargetModel, designTime: true);
        var additions = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(redesign.GetRelationalModel(), target.GetRelationalModel());
        additions.Should().HaveCount(3).And.OnlyContain(operation =>
            operation is CreateTableOperation || operation is CreateIndexOperation || operation is InsertDataOperation);
        additions.OfType<CreateTableOperation>().Should().ContainSingle()
            .Which.Name.Should().Be("AppFeatureFlagAccounts");
        additions.OfType<InsertDataOperation>().Should().ContainSingle()
            .Which.Table.Should().Be("AppFeatureFlags");
        context.Database.HasPendingModelChanges().Should().BeFalse();
        context.Database.GetMigrations().Should().ContainInOrder(
            "20261001074038_AddHabitSkipUndo", "20261017011939_AddFeatureFlagAccounts");
    }

    [Fact]
    public void Up_AddsCompositeKeyCascadeRelationshipsAndDisabledSeed()
    {
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(AddFeatureFlagAccounts).GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(new AddFeatureFlagAccounts(), [builder]);

        var table = builder.Operations.OfType<CreateTableOperation>().Should().ContainSingle().Subject;
        table.Name.Should().Be("AppFeatureFlagAccounts");
        table.PrimaryKey!.Columns.Should().Equal("FlagKey", "UserId");
        table.ForeignKeys.Should().HaveCount(2).And.OnlyContain(key => key.OnDelete == ReferentialAction.Cascade);
        table.Columns.Should().Contain(column => column.Name == "CreatedAtUtc" && !column.IsNullable);
        var seed = builder.Operations.OfType<InsertDataOperation>().Should().ContainSingle().Subject;
        seed.Values[0, Array.IndexOf(seed.Columns, "Key")].Should().Be("redesign");
        seed.Values[0, Array.IndexOf(seed.Columns, "Enabled")].Should().Be(false);
    }

    [Fact]
    public async Task DeletingFlag_CascadesAccountListInDatabase()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        var user = User.Create("Account", "account@example.com").Value;
        context.Users.Add(user);
        context.AppFeatureFlagAccounts.Add(AppFeatureFlagAccount.Create("redesign", user.Id));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await context.AppFeatureFlags.Where(flag => flag.Key == "redesign").ExecuteDeleteAsync();

        (await context.AppFeatureFlagAccounts.CountAsync()).Should().Be(0);
    }
}
