using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class CascadeRetiredFeatureParentsMigrationTests
{
    internal static readonly (string Table, string Column, string PrincipalTable)[] ForeignKeys =
    [
        ("BlockedUsers", "BlockedId", "Users"),
        ("BlockedUsers", "BlockerId", "Users"),
        ("ChallengeParticipants", "UserId", "Users"),
        ("Challenges", "CreatorId", "Users"),
        ("Cheers", "HabitId", "Habits"),
        ("Cheers", "RecipientId", "Users"),
        ("Cheers", "SenderId", "Users"),
        ("FriendFeedEvents", "ActorUserId", "Users"),
        ("Friendships", "AddresseeId", "Users"),
        ("Friendships", "RequesterId", "Users"),
        ("Reports", "ReportedUserId", "Users"),
        ("Reports", "ReporterId", "Users"),
        ("UserFacts", "UserId", "Users")
    ];

    public static IEnumerable<object[]> ForeignKeyCases() =>
        ForeignKeys.Select(key => new object[] { key.Table, key.Column, key.PrincipalTable });

    public static IEnumerable<object[]> PreviousForeignKeyCases() =>
        ForeignKeyCases().Where(key => (string)key[0] != "UserFacts");

    [Theory]
    [MemberData(nameof(ForeignKeyCases))]
    public void Up_AddsCascadeWithoutValidatingExistingRows(string table, string column, string principalTable)
    {
        var migration = new CascadeRetiredFeatureParents();

        migration.UpOperations.OfType<SqlOperation>().Select(operation => operation.Sql).Should().ContainSingle(sql =>
            sql == $"ALTER TABLE \"{table}\" ADD CONSTRAINT \"FK_{table}_{principalTable}_{column}\" "
                + $"FOREIGN KEY (\"{column}\") REFERENCES \"{principalTable}\" (\"Id\") ON DELETE CASCADE NOT VALID;");
    }

    [Theory]
    [MemberData(nameof(PreviousForeignKeyCases))]
    public void Down_RestoresPreviousDeleteActionWithoutValidatingExistingRows(string table, string column, string principalTable)
    {
        var migration = new CascadeRetiredFeatureParents();
        var action = principalTable == "Habits" ? "SET NULL" : "RESTRICT";

        migration.DownOperations.OfType<SqlOperation>().Select(operation => operation.Sql).Should().ContainSingle(sql =>
            sql == $"ALTER TABLE \"{table}\" ADD CONSTRAINT \"FK_{table}_{principalTable}_{column}\" "
                + $"FOREIGN KEY (\"{column}\") REFERENCES \"{principalTable}\" (\"Id\") ON DELETE {action} NOT VALID;");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Migration_OnlyChangesExpectedConstraintsWithinTransaction(bool down)
    {
        var migration = new CascadeRetiredFeatureParents();
        var operations = down ? migration.DownOperations : migration.UpOperations;
        var droppedKeys = down ? ForeignKeys : ForeignKeys.Where(key => key.Table != "UserFacts");

        operations.Should().HaveCount(25).And.OnlyContain(operation =>
            operation is DropForeignKeyOperation || operation is SqlOperation);
        operations.OfType<SqlOperation>().Should().HaveCount(down ? 12 : 13)
            .And.OnlyContain(operation => !operation.SuppressTransaction
                && operation.Sql.StartsWith("ALTER TABLE \"", StringComparison.Ordinal)
                && operation.Sql.Contains(" ADD CONSTRAINT \"", StringComparison.Ordinal)
                && !operation.Sql.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase)
                && !operation.Sql.Contains("UPDATE ", StringComparison.OrdinalIgnoreCase));
        operations.OfType<DropForeignKeyOperation>().Select(key => new { key.Table, key.Name }).Should()
            .BeEquivalentTo(droppedKeys.Select(key => new
            {
                key.Table,
                Name = $"FK_{key.Table}_{key.PrincipalTable}_{key.Column}"
            }));
        operations.Take(down ? 13 : 12).Should().OnlyContain(operation => operation is DropForeignKeyOperation);
    }

    [Fact]
    public void Down_RemovesUserFactsConstraintWithoutRecreatingIt()
    {
        var migration = new CascadeRetiredFeatureParents();

        migration.DownOperations.OfType<DropForeignKeyOperation>().Should().ContainSingle(key =>
            key.Table == "UserFacts" && key.Name == "FK_UserFacts_Users_UserId");
        migration.DownOperations.OfType<SqlOperation>().Should().NotContain(operation => operation.Sql.Contains("UserFacts"));
    }

    [Fact]
    public void CascadeModel_HasNoPendingChanges()
    {
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);

        context.Database.HasPendingModelChanges().Should().BeFalse();
    }
}
