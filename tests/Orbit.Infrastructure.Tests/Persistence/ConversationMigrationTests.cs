using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class ConversationMigrationTests
{
    [Fact]
    public void Migration_ExpandsSchemaAndDisablesCollection_AndDownDropsDiagnosticData()
    {
        var migration = new PersistConversationTurns();
        var table = migration.UpOperations.OfType<CreateTableOperation>().Should().ContainSingle().Subject;
        table.Name.Should().Be("ConversationTurns");
        table.Columns.Where(column => column.Name is "Content" or "ToolCallsJson")
            .Should().OnlyContain(column => column.ColumnType == "text");
        table.ForeignKeys.Should().ContainSingle().Which.OnDelete.Should().Be(ReferentialAction.Cascade);
        migration.UpOperations.OfType<CreateIndexOperation>().Should().HaveCount(3)
            .And.OnlyContain(index => !index.Columns.Contains("Content") && !index.Columns.Contains("ToolCallsJson"));
        var flag = migration.UpOperations.OfType<InsertDataOperation>().Should().ContainSingle().Subject;
        flag.Values[0, Array.IndexOf(flag.Columns, "Key")].Should().Be("astra_conversation_recording");
        flag.Values[0, Array.IndexOf(flag.Columns, "Enabled")].Should().Be(false);
        migration.DownOperations.OfType<DropTableOperation>().Should().ContainSingle().Which.Name.Should().Be("ConversationTurns");
        migration.DownOperations.OfType<DropColumnOperation>().Should().ContainSingle().Which.Name.Should().Be("ConversationHistoryClearedAtUtc");
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        context.Database.HasPendingModelChanges().Should().BeFalse();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var model = context.GetService<IDesignTimeModel>().Model;
        generator.Generate(migration.UpOperations, model).Should().NotBeEmpty();
        generator.Generate(migration.DownOperations, model).Should().NotBeEmpty();
    }
}
