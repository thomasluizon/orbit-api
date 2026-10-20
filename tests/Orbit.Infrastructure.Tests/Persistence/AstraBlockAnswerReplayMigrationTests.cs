using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class AstraBlockAnswerReplayMigrationTests
{
    [Fact]
    public void ReplayMigration_AddsOnlyNullableAnswerColumns()
    {
        var migration = new AddAstraBlockAnswerReplay();
        migration.UpOperations.Should().HaveCount(4);
        var columns = migration.UpOperations.OfType<AddColumnOperation>().ToArray();
        columns.Select(column => column.Name).Should().BeEquivalentTo(
            "AnswerJson", "CommittedResponseJson", "ReservedAllowanceDate", "ResponseJson");
        columns.Should().OnlyContain(column => column.Table == "IssuedAstraBlocks" && column.IsNullable);
        columns.Single(column => column.Name == "ResponseJson").ColumnType.Should().Be("text");
    }

    [Fact]
    public void ReplayTargetAndSnapshot_IncludeBothMergedModels()
    {
        var migration = new AddAstraBlockAnswerReplay();
        var snapshot = new OrbitDbContextModelSnapshot();
        foreach (var model in new[] { migration.TargetModel, snapshot.Model })
        {
            model.FindEntityType(typeof(OwedSubscriptionCancellation).FullName!).Should().NotBeNull();
            model.FindEntityType(typeof(IssuedAstraBlock).FullName!)!
                .FindProperty(nameof(IssuedAstraBlock.AnswerJson)).Should().NotBeNull();
        }
    }
}
