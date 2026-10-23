using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class AppStoreMigrationTests
{
    [Fact]
    public void MigrationAndSnapshot_AddNullableBindingsAndUniqueNotificationLedger()
    {
        var migration = new AddAppStoreSubscriptions();
        migration.UpOperations.OfType<AddColumnOperation>().Should().HaveCount(3).And.OnlyContain(column => column.IsNullable);
        migration.UpOperations.Should().NotContain(operation => operation is DropColumnOperation || operation is DropTableOperation || operation is AlterColumnOperation);
        migration.UpOperations.OfType<CreateIndexOperation>().Should().Contain(index => index.Name == "IX_Users_AppStoreOriginalTransactionId" && index.IsUnique);
        var snapshot = new OrbitDbContextModelSnapshot().Model;
        var user = snapshot.FindEntityType(typeof(User).FullName!)!;
        user.FindProperty(nameof(User.AppStoreOriginalTransactionId))!.IsNullable.Should().BeTrue();
        snapshot.FindEntityType(typeof(ProcessedAppStoreNotification).FullName!)!.GetIndexes().Should().Contain(index => index.IsUnique);
    }
}
