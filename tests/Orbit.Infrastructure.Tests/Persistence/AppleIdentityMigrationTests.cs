using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Migrations;

namespace Orbit.Infrastructure.Tests.Persistence;

public class AppleIdentityMigrationTests
{
    [Fact]
    public void MigrationAndSnapshot_AddNullableCredentialsAndUniqueSubject()
    {
        var migration = new AddAppleSignInCredentials();
        migration.UpOperations.OfType<AddColumnOperation>().Should().HaveCount(3).And.OnlyContain(column => column.IsNullable);
        migration.UpOperations.Should().NotContain(operation => operation is DropColumnOperation || operation is AlterColumnOperation);
        migration.UpOperations.OfType<CreateIndexOperation>().Should().Contain(index => index.Name == "IX_Users_AppleSubject" && index.IsUnique);
        var user = new OrbitDbContextModelSnapshot().Model.FindEntityType(typeof(User).FullName!)!;
        user.FindProperty(nameof(User.AppleRefreshToken))!.IsNullable.Should().BeTrue();
        user.GetIndexes().Should().Contain(index => index.IsUnique && index.Properties.Single().Name == nameof(User.AppleSubject));
    }
}
