using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Migrations;

namespace Orbit.Infrastructure.Tests.Persistence;

public sealed class ProactiveMigrationTests
{
    [Fact]
    public void Migration_OnlyRelaxesStoredSettingAndReplacesTheLedger()
    {
        var operations = new ReplaceProactiveCheckinsWithItems().UpOperations;
        var setting = operations.OfType<AlterColumnOperation>().Single(o => o.Name == "ProactiveAstraEnabled");
        setting.IsNullable.Should().BeTrue();
        setting.OldColumn.IsNullable.Should().BeFalse();
        setting.DefaultValue.Should().BeNull();
        operations.OfType<SqlOperation>().Should().BeEmpty();
        operations.OfType<DropTableOperation>().Select(o => o.Name).Should().Contain("SentProactiveCheckins");
        operations.OfType<CreateTableOperation>().Select(o => o.Name).Should().Contain("ProactiveItems");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task StoredStates_RoundTripWithoutLosingTheChoice(bool? choice)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Person", "person@example.com").Value;
        if (choice.HasValue) user.SetProactiveAstraEnabled(choice.Value);
        factory.Context.Users.Add(user);
        await factory.Context.SaveChangesAsync();
        factory.Context.ChangeTracker.Clear();
        (await factory.Context.Users.SingleAsync()).ProactiveAstraEnabled.Should().Be(choice);
    }
}
