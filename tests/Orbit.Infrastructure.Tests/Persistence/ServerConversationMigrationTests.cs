using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class ServerConversationMigrationTests
{
    [Fact]
    public void Migration_OnlyExtendsConversationStoreAndPrivacyDescription_AndMatchesSnapshot()
    {
        var migration = new PersistServerConversations();
        var added = migration.UpOperations.OfType<AddColumnOperation>().ToArray();
        added.Should().HaveCount(7).And.OnlyContain(column => column.Table == "ConversationTurns");
        added.Select(column => column.Name).Should().BeEquivalentTo("ConversationId", "TurnOrder", "ToolResultsJson", "PreviewOutcomesJson", "ImageObjectKey", "ImageContentType", "ImageSha256");
        added.Where(column => !column.IsNullable).Should().OnlyContain(column => Equals(column.DefaultValue, "[]"));
        migration.UpOperations.OfType<CreateIndexOperation>().Should().HaveCount(2);
        migration.UpOperations.OfType<UpdateDataOperation>().Should().ContainSingle().Which.KeyValues[0, 0].Should().Be("astra_conversation_recording");
        migration.DownOperations.OfType<DropColumnOperation>().Select(column => column.Name).Should().BeEquivalentTo(added.Select(column => column.Name));
        using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>().UseNpgsql("Host=unused;Database=unused").Options);
        db.Database.HasPendingModelChanges().Should().BeFalse();
        var initializer = db.GetService<IModelRuntimeInitializer>();
        var previous = initializer.Initialize(new LinkPendingOperationsToConversation().TargetModel, designTime: true);
        var target = initializer.Initialize(migration.TargetModel, designTime: true);
        var difference = db.GetService<IMigrationsModelDiffer>().GetDifferences(previous.GetRelationalModel(), target.GetRelationalModel());
        difference.OfType<AddColumnOperation>().Select(column => column.Name).Should().BeEquivalentTo(added.Select(column => column.Name));
        var sql = db.GetService<IMigrationsSqlGenerator>();
        sql.Generate(migration.UpOperations, target).Should().NotBeEmpty();
        sql.Generate(migration.DownOperations, previous).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Migration_UpAndDown_RunOnExistingSqliteTestDatabase()
    {
        using var database = new SqliteOrbitDbContextFactory();
        var db = database.Context;
        var user = Orbit.Domain.Entities.User.Create("Migration", "migration@example.com").Value;
        db.Users.Add(user);
        db.ConversationTurns.Add(Orbit.Domain.Entities.ConversationTurn.Create(user.Id, Guid.NewGuid(), "user", DateTime.UtcNow,
            null, "Retained legacy conversation", "[]").Value);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var migration = new PersistServerConversations();
        var generator = db.GetService<IMigrationsSqlGenerator>();
        var currentModel = db.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        using var previous = new LegacyConversationContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseSqlite(db.Database.GetDbConnection()).Options);
        var previousModel = previous.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        foreach (var command in generator.Generate(migration.DownOperations, previousModel))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(migration.UpOperations, currentModel))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
        (await db.AppFeatureFlags.SingleAsync(flag => flag.Key == "astra_conversation_recording")).Description
            .Should().Contain("server history");
        var retained = await db.ConversationTurns.SingleAsync();
        retained.Content.Should().Be("Retained legacy conversation");
        retained.ToolResultsJson.Should().Be("[]");
        retained.PreviewOutcomesJson.Should().Be("[]");
    }
    private sealed class LegacyConversationContext(DbContextOptions<OrbitDbContext> options) : OrbitDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            var turn = modelBuilder.Entity<Orbit.Domain.Entities.ConversationTurn>();
            turn.Ignore(row => row.ConversationId);
            turn.Ignore(row => row.TurnOrder);
            turn.Ignore(row => row.ToolResultsJson);
            turn.Ignore(row => row.PreviewOutcomesJson);
            turn.Ignore(row => row.ImageObjectKey);
            turn.Ignore(row => row.ImageContentType);
            turn.Ignore(row => row.ImageSha256);
        }
    }
}
