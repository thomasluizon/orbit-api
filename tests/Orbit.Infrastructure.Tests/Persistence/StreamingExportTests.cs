using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Goals.Services;
using Orbit.Application.Profile.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class StreamingExportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_StreamsTenThousandTurnsInBoundedBatchesAndKeepsTheContent(bool duplicateSortKeys)
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var db = factory.Context;
        var user = User.Create("Export", "export-stream@example.test").Value;
        db.Users.Add(user);
        for (var index = 0; index < 150; index++)
        {
            var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Read " + index, FrequencyUnit.Day, 1, new DateOnly(2026, 6, 1))).Value;
            db.Habits.Add(habit);
            for (var day = 0; day < 10; day++)
                db.HabitLogs.Add(habit.Log(new DateOnly(2026, 6, 1).AddDays(day), new string('x', 512)).Value);
        }
        var instant = DateTime.UtcNow;
        for (var index = 0; index < 10_000; index++)
            db.ConversationTurns.Add(ConversationTurn.Create(user.Id, Guid.NewGuid(), "user", instant.AddSeconds(duplicateSortKeys ? 0 : index), null, "Text " + index, "[]").Value);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var repos = new ExportUserDataRepositories(new GenericRepository<User>(db), new GenericRepository<Habit>(db),
            new GenericRepository<HabitLog>(db), new GenericRepository<Goal>(db), new GenericRepository<GoalProgressLog>(db),
            new GenericRepository<Tag>(db), new GenericRepository<Notification>(db), new GenericRepository<ChecklistTemplate>(db),
            new GenericRepository<UserAchievement>(db), new GenericRepository<StreakFreeze>(db), new GenericRepository<Referral>(db),
            new GenericRepository<ApiKey>(db), new GenericRepository<ProactiveItem>(db), new GenericRepository<NotionConnection>(db),
            new GenericRepository<NotionTask>(db), new GenericRepository<ConversationTurn>(db), new GenericRepository<ConversationOffer>(db));
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 6, 1));
        var progress = Substitute.For<IGoalProgressReadSyncer>();
        progress.ComputeFreshValuesAsync(user.Id, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, int>());
        var original = await new ExportUserDataQueryHandler(repos, dates, progress).Handle(new(user.Id), default);
        var result = await new StreamUserDataQueryHandler(repos, dates, progress).Handle(new(user.Id), default);
        counter.Reset();
        using var destination = new MemoryStream();
        using var transport = new AsyncOnlyWriteStream(destination);
        await result.Value.WriteAsync(transport, default);
        transport.AsyncWrites.Should().BeGreaterThan(10_000);
        transport.LargestWrite.Should().BeLessThan(64 * 1024);
        destination.Length.Should().BeGreaterThan(1024 * 1024);
        var conversationReads = counter.Commands.Where(c => c.Sql.Contains("\"ConversationTurns\"", StringComparison.Ordinal)).ToArray();
        conversationReads.Should().HaveCount(101);
        conversationReads.Should().OnlyContain(c => c.Sql.Contains("LIMIT", StringComparison.Ordinal));
        foreach (var read in conversationReads)
            read.Sql[read.Sql.IndexOf("ORDER BY", StringComparison.Ordinal)..].Should().Contain("\"Id\"");
        using var actual = JsonDocument.Parse(destination.ToArray());
        var expected = JsonSerializer.SerializeToElement(original.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var property in expected.EnumerateObject().Where(p => p.Name != "exportedAtUtc"))
        {
            if (duplicateSortKeys && property.Name == "conversations")
                actual.RootElement.GetProperty(property.Name).EnumerateArray().Select(row => row.GetProperty("turnId").GetGuid())
                    .Should().BeEquivalentTo(property.Value.EnumerateArray().Select(row => row.GetProperty("turnId").GetGuid()));
            else JsonElement.DeepEquals(property.Value, actual.RootElement.GetProperty(property.Name)).Should().BeTrue(property.Name);
        }
        db.ChangeTracker.Entries().Where(entry => entry.Entity is not User).Should().BeEmpty();
    }
}
