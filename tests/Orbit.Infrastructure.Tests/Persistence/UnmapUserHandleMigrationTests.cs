using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class UnmapUserHandleMigrationTests
{
    [Fact]
    public void UpAndDown_PreserveDatabaseSchemaDuringInstanceDrain()
    {
        var migration = new UnmapUserHandle();

        migration.UpOperations.Should().BeEmpty();
        migration.DownOperations.Should().BeEmpty();
    }

    [Fact]
    public void TargetModel_UnmapsOnlyHandle()
    {
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var previous = initializer.Initialize(new DropSentProactiveCheckins().TargetModel, designTime: true);
        var target = initializer.Initialize(new UnmapUserHandle().TargetModel, designTime: true);
        var changes = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(previous.GetRelationalModel(), target.GetRelationalModel());

        var column = changes.Should().ContainSingle().Which.Should().BeOfType<DropColumnOperation>().Subject;
        column.Table.Should().Be("Users");
        column.Name.Should().Be("Handle");
    }
}
