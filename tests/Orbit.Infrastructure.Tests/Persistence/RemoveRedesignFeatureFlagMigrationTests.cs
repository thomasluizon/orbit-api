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

public sealed class RemoveRedesignFeatureFlagMigrationTests
{
    [Fact]
    public void Model_SeedsNoRedesignFlagAndMatchesSnapshot()
    {
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var model = context.GetService<IDesignTimeModel>().Model;

        model.FindEntityType(typeof(AppFeatureFlag))!.GetSeedData()
            .Select(flag => flag[nameof(AppFeatureFlag.Key)]).Should().NotContain("redesign");
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Fact]
    public void Up_DeletesAccountTargetsBeforeFlagWithoutSchemaContraction()
    {
        var operations = new RemoveRedesignFeatureFlag().UpOperations;

        operations.Should().HaveCount(2);
        operations[0].Should().BeOfType<SqlOperation>().Which.Sql.Should().Be(
            """DELETE FROM "AppFeatureFlagAccounts" WHERE "FlagKey" = 'redesign';""");
        var deletion = operations[1].Should().BeOfType<DeleteDataOperation>().Subject;
        deletion.Table.Should().Be("AppFeatureFlags");
        deletion.KeyColumns.Should().Equal("Key");
        deletion.KeyValues[0, 0].Should().Be("redesign");
        RollingMigrationGate.Check(nameof(RemoveRedesignFeatureFlag), operations).Should().BeEmpty();
    }

    [Fact]
    public async Task Migration_RemovesOnlyRetiredFlagAndTargetsAndRestoresOriginalSeed()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        var accountA = User.Create("Account A", "migration-a@example.com").Value;
        var accountB = User.Create("Account B", "migration-b@example.com").Value;
        context.Users.AddRange(accountA, accountB);
        context.AppFeatureFlags.Add(AppFeatureFlag.Create("redesign", true, "Pro", "Changed by an administrator"));
        context.AppFeatureFlagAccounts.AddRange(
            AppFeatureFlagAccount.Create("redesign", accountA.Id),
            AppFeatureFlagAccount.Create("redesign", accountB.Id),
            AppFeatureFlagAccount.Create("astra_eval_trace", accountA.Id));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var retainedFlags = await context.AppFeatureFlags.AsNoTracking()
            .Where(flag => flag.Key != "redesign").ToArrayAsync();
        var migration = new RemoveRedesignFeatureFlag { ActiveProvider = "Microsoft.EntityFrameworkCore.Sqlite" };
        var model = context.GetService<IDesignTimeModel>().Model;
        var generator = context.GetService<IMigrationsSqlGenerator>();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            foreach (var command in generator.Generate(migration.UpOperations, model))
                await context.Database.ExecuteSqlRawAsync(command.CommandText);

            (await context.AppFeatureFlags.AsNoTracking().ToArrayAsync()).Should().BeEquivalentTo(retainedFlags);
            var target = (await context.AppFeatureFlagAccounts.AsNoTracking().ToArrayAsync())
                .Should().ContainSingle().Subject;
            target.FlagKey.Should().Be("astra_eval_trace");
            target.UserId.Should().Be(accountA.Id);
            (await context.Users.CountAsync()).Should().Be(2);
        }

        foreach (var command in generator.Generate(migration.DownOperations, model))
            await context.Database.ExecuteSqlRawAsync(command.CommandText);

        var restored = await context.AppFeatureFlags.AsNoTracking().SingleAsync(flag => flag.Key == "redesign");
        restored.Enabled.Should().BeFalse();
        restored.PlanRequirement.Should().BeNull();
        restored.Description.Should().Be("Serve the redesigned web and Android builds to this account");
        restored.UpdatedAtUtc.Should().Be(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));
        (await context.AppFeatureFlagAccounts.CountAsync(target => target.FlagKey == "redesign")).Should().Be(0);
        (await context.AppFeatureFlags.AsNoTracking().Where(flag => flag.Key != "redesign").ToArrayAsync())
            .Should().BeEquivalentTo(retainedFlags);
    }
}
