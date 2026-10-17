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
        setting.OldColumn.DefaultValue.Should().Be(false, "the original migration installed a database default that must be removed");
        setting.DefaultValue.Should().BeNull();
        operations.OfType<SqlOperation>().Should().BeEmpty();
        operations.OfType<DropTableOperation>().Select(o => o.Name).Should().Contain("SentProactiveCheckins");
        operations.OfType<CreateTableOperation>().Select(o => o.Name).Should().Contain("ProactiveItems");
    }

    [Fact]
    public async Task UniqueIndexes_AllowUnpushedItemsAndRejectDuplicateTriggersAndPushes()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("Person", "person@example.com").Value;
        db.Users.Add(user);
        var now = new DateTime(2030, 10, 14, 19, 0, 0, DateTimeKind.Utc);
        var date = DateOnly.FromDateTime(now);
        var evening = Services.ProactiveEngineServiceTests.Item(user.Id, Orbit.Domain.Enums.ProactiveTrigger.EveningCheckin, date, now);
        evening.Push(now);
        db.ProactiveItems.Add(evening);
        db.ProactiveItems.Add(Services.ProactiveEngineServiceTests.Item(user.Id, Orbit.Domain.Enums.ProactiveTrigger.MorningPlan, date, now));
        await db.SaveChangesAsync();
        var busy = Services.ProactiveEngineServiceTests.Item(user.Id, Orbit.Domain.Enums.ProactiveTrigger.BusyCalendarDay, date, now);
        busy.Push(now);
        db.ProactiveItems.Add(busy);
        Func<Task> secondPush = () => db.SaveChangesAsync();
        await secondPush.Should().ThrowAsync<DbUpdateException>();
        db.Entry(busy).State = EntityState.Detached;
        db.ProactiveItems.Add(Services.ProactiveEngineServiceTests.Item(user.Id, Orbit.Domain.Enums.ProactiveTrigger.EveningCheckin, date, now));
        Func<Task> duplicate = () => db.SaveChangesAsync();
        await duplicate.Should().ThrowAsync<DbUpdateException>();
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
