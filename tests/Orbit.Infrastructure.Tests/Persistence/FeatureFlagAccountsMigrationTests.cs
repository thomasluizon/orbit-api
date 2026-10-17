using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Migrations;

namespace Orbit.Infrastructure.Tests.Persistence;

public class FeatureFlagAccountsMigrationTests
{
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
