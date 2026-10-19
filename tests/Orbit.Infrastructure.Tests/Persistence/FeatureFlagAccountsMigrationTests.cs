using System.Reflection;
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

public class FeatureFlagAccountsMigrationTests
{
    [Fact]
    public void TargetModel_PreservesPromotedRedesignAndCombinedSnapshotMatchesCurrentModel()
    {
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var target = initializer.Initialize(new AddDurableSignupWork().TargetModel, designTime: true);
        var featureFlags = initializer.Initialize(new AddFeatureFlagAccounts().TargetModel, designTime: true);
        var current = context.GetService<IDesignTimeModel>().Model;

        var differences = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(target.GetRelationalModel(), current.GetRelationalModel());

        differences.Where(operation => operation is DropForeignKeyOperation || operation is AddForeignKeyOperation)
            .Should().HaveCount(4);
        differences.OfType<AlterColumnOperation>().Should().ContainSingle(column =>
            column.Table == "HabitLogs" && column.Name == "IsSlip"
            && !column.IsNullable && column.OldColumn.IsNullable);
        var renewalColumns = differences.OfType<AddColumnOperation>()
            .Where(column => column.Table == "Users").ToArray();
        renewalColumns.Should().HaveCount(2).And.OnlyContain(column =>
            column.ClrType == typeof(bool) && !column.IsNullable
            && Equals(column.DefaultValue, false));
        renewalColumns.Select(column => column.Name).Should().BeEquivalentTo(
            "StripeRenewalStoppedByDeletion", "PlayRenewalStoppedByDeletion");
        differences.OfType<DropForeignKeyOperation>().Select(operation => operation.Name).Should().BeEquivalentTo(
            "FK_AccountabilityPairs_Users_AddresseeId", "FK_AccountabilityPairs_Users_RequesterId");
        differences.OfType<AddForeignKeyOperation>().Should().HaveCount(2).And.OnlyContain(operation =>
            operation.Table == "AccountabilityPairs" && operation.PrincipalTable == "Users"
            && operation.OnDelete == ReferentialAction.Cascade);
        differences.OfType<AddForeignKeyOperation>().Select(operation => operation.Name).Should().BeEquivalentTo(
            "FK_AccountabilityPairs_Users_AddresseeId", "FK_AccountabilityPairs_Users_RequesterId");
        var redesign = initializer.Initialize(new AddHabitSkipUndo().TargetModel, designTime: true);
        var additions = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(redesign.GetRelationalModel(), featureFlags.GetRelationalModel());
        additions.Should().HaveCount(3).And.OnlyContain(operation =>
            operation is CreateTableOperation || operation is CreateIndexOperation || operation is InsertDataOperation);
        additions.OfType<CreateTableOperation>().Should().ContainSingle()
            .Which.Name.Should().Be("AppFeatureFlagAccounts");
        additions.OfType<InsertDataOperation>().Should().ContainSingle()
            .Which.Table.Should().Be("AppFeatureFlags");
        var signupAdditions = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(featureFlags.GetRelationalModel(), target.GetRelationalModel());
        signupAdditions.Should().HaveCount(6).And.OnlyContain(operation =>
            operation is AddColumnOperation && !operation.IsDestructiveChange);
        var signupColumns = signupAdditions.Cast<AddColumnOperation>().ToArray();
        signupColumns.Should().OnlyContain(column => column.Table == "Users" && column.IsNullable);
        signupColumns.Select(column => column.Name).Should().BeEquivalentTo(
            "SignupLanguage", "SignupReferralCode", "SignupCompletedAtUtc",
            "SignupReferralCompletedAtUtc", "SignupAnalyticsCompletedAtUtc", "SignupWelcomeCompletedAtUtc");
        context.Database.HasPendingModelChanges().Should().BeFalse();
        context.Database.GetMigrations().Should().ContainInOrder(
            "20261001074038_AddHabitSkipUndo",
            "20261017011939_AddFeatureFlagAccounts",
            "20261017083409_ReplaceProactiveCheckinsWithItems",
            "20261017173456_AddIssuedAstraBlocks",
            "20261017180536_AddNotionConnection",
            "20261017182405_TrackNotionOAuthStateConsumption",
            "20261018093533_CascadeAccountabilityPairsOnUserDeletion",
            "20261018111350_AddDurableSignupWork",
            "20261018111711_RecordDeletionStoppedRenewals",
            "20261019011031_ContractHabitLogSlip");
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
