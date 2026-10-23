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

public class DropRetiredFeatureTablesMigrationTests
{
    private static readonly string[] RetiredTables =
    [
        "Challenges", "ChallengeParticipants", "ChallengeParticipantHabits",
        "AccountabilityPairs", "AccountabilityPairHabits", "AccountabilityCheckIns",
        "Friendships", "FriendFeedEvents", "Cheers", "BlockedUsers", "Reports",
        "UserFacts", "AiFactExtractionBatches"
    ];

    [Fact]
    public void TargetModel_DiffersFromRuntimeOnlyByRetiredLedger()
    {
        using var context = CreateContext();
        var target = context.GetService<IModelRuntimeInitializer>()
            .Initialize(new DropRetiredFeatureTables().TargetModel, designTime: true);
        var current = context.GetService<IDesignTimeModel>().Model;

        var differences = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(target.GetRelationalModel(), current.GetRelationalModel());
        differences.Should().ContainSingle();
        DropSentProactiveCheckinsMigrationTests.AssertRetiredLedgerDrop(differences);
    }

    [Fact]
    public void Up_DropsExactlyRetiredTablesAndMemoryLimit()
    {
        var operations = new DropRetiredFeatureTables().UpOperations;

        operations.Should().HaveCount(14).And.OnlyContain(operation =>
            operation is DropTableOperation || operation is DeleteDataOperation);
        operations.OfType<DropTableOperation>().Select(table => table.Name)
            .Should().BeEquivalentTo(RetiredTables);
        var seed = operations.OfType<DeleteDataOperation>().Should().ContainSingle().Subject;
        seed.Table.Should().Be("AppConfigs");
        seed.KeyColumns.Should().Equal("Key");
        seed.KeyValues.GetLength(0).Should().Be(1);
        seed.KeyValues[0, 0].Should().Be("MaxUserFacts");
    }

    [Fact]
    public void Model_RemovesRetiredTypesAndSeedWhilePreservingHandleAndEveryOtherContract()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var previous = context.GetService<IModelRuntimeInitializer>()
            .Initialize(new PreserveProviderPaidCoverage().TargetModel, designTime: true);
        var differences = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(previous.GetRelationalModel(), model.GetRelationalModel());
        var generator = context.GetService<IMigrationsSqlGenerator>();

        var retiredFeatureChanges = differences
            .Where(operation => operation is not DropTableOperation { Name: "SentProactiveCheckins" }).ToArray();
        generator.Generate(retiredFeatureChanges, model).Select(command => command.CommandText)
            .Should().Equal(generator.Generate(new DropRetiredFeatureTables().UpOperations, model)
                .Select(command => command.CommandText));
        model.GetEntityTypes().Select(entity => entity.GetTableName())
            .Should().NotIntersectWith(RetiredTables);
        foreach (var entity in previous.GetEntityTypes().Where(entity => RetiredTables.Contains(entity.GetTableName())))
            typeof(User).Assembly.GetType(entity.Name).Should().BeNull();
        typeof(OrbitDbContext).GetProperties().Select(property => property.Name)
            .Should().NotIntersectWith(RetiredTables);
        model.FindEntityType(typeof(User))!.FindProperty(nameof(User.Handle)).Should().NotBeNull();
        model.FindEntityType(typeof(AppConfig))!.GetSeedData()
            .Should().NotContain(row => Equals(row["Key"], "MaxUserFacts"));
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Fact]
    public void Down_RecreatesPreviousSchemaWithAllColumnsKeysIndexesAndMemoryLimit()
    {
        using var context = CreateContext();
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var previous = initializer.Initialize(new PreserveProviderPaidCoverage().TargetModel, designTime: true);
        var current = initializer.Initialize(new DropRetiredFeatureTables().TargetModel, designTime: true);
        var expected = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(current.GetRelationalModel(), previous.GetRelationalModel());
        var operations = new DropRetiredFeatureTables().DownOperations;
        var generator = context.GetService<IMigrationsSqlGenerator>();

        operations.Should().OnlyContain(operation => operation is CreateTableOperation
            || operation is CreateIndexOperation || operation is InsertDataOperation);
        operations.OfType<CreateTableOperation>().Select(table => table.Name)
            .Should().BeEquivalentTo(RetiredTables);
        operations.OfType<InsertDataOperation>().Should().ContainSingle()
            .Which.Table.Should().Be("AppConfigs");
        generator.Generate(operations, previous).Select(command => command.CommandText)
            .Should().Equal(generator.Generate(expected, previous).Select(command => command.CommandText));
    }

    private static OrbitDbContext CreateContext() => new(new DbContextOptionsBuilder<OrbitDbContext>()
        .UseNpgsql("Host=unused;Database=unused").Options);
}
