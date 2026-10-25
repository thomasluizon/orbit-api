using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class OAuthClientIdentityMigrationTests
{
    [Fact]
    public void Migration_AddsOnlyNullableClientIdentity_AndMatchesModelTransition()
    {
        var migration = new TrackOAuthClientIdentity();
        var column = migration.UpOperations.Should().ContainSingle().Which.Should().BeOfType<AddColumnOperation>().Subject;
        column.Table.Should().Be("ApiKeys");
        column.Name.Should().Be("OAuthClientId");
        column.ClrType.Should().Be<string>();
        column.MaxLength.Should().Be(1024);
        column.IsNullable.Should().BeTrue();
        column.DefaultValue.Should().BeNull();
        var removed = migration.DownOperations.Should().ContainSingle().Which.Should().BeOfType<DropColumnOperation>().Subject;
        removed.Table.Should().Be("ApiKeys");
        removed.Name.Should().Be("OAuthClientId");

        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var previous = initializer.Initialize(new AddTransactionCommitVerification().TargetModel, designTime: true);
        var target = initializer.Initialize(migration.TargetModel, designTime: true);
        var differ = context.GetService<IMigrationsModelDiffer>();
        differ.GetDifferences(previous.GetRelationalModel(), target.GetRelationalModel())
            .Should().BeEquivalentTo(migration.UpOperations, options => options.PreferringRuntimeMemberTypes());
        var snapshot = initializer.Initialize(new OrbitDbContextModelSnapshot().Model, designTime: true);
        differ.GetDifferences(context.GetService<IDesignTimeModel>().Model.GetRelationalModel(), snapshot.GetRelationalModel())
            .Should().BeEmpty();
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }
}
