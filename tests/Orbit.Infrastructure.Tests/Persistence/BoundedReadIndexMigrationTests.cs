using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class BoundedReadIndexMigrationTests
{
    [Fact]
    public void Migration_AddsReminderAndUserIndexesWithoutChangingTheUsageUniqueKey()
    {
        var migration = new RestoreBoundedReadIndexes();
        migration.UpOperations.Should().HaveCount(2).And.OnlyContain(operation => operation is CreateIndexOperation);
        var indexes = migration.UpOperations.Cast<CreateIndexOperation>().ToDictionary(index => index.Name);
        indexes["IX_Habits_Reminder"].Columns.Should().Equal("ReminderEnabled", "IsCompleted");
        indexes["IX_Habits_Reminder"].Filter.Should().Be("\"ReminderEnabled\" AND NOT \"IsCompleted\"");
        indexes["IX_AiUsageDaily_UserId"].Columns.Should().Equal("UserId");
        indexes.Values.Should().OnlyContain(index => !index.IsUnique);
        using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        db.Database.HasPendingModelChanges().Should().BeFalse();
    }
}
