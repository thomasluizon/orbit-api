using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Application.Notifications;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;
using Xunit.Abstractions;

namespace Orbit.Infrastructure.Tests.Controllers;

public class BulkHabitsPerformanceTests(ITestOutputHelper output)
{
    private static readonly DateOnly Today = new(2026, 6, 1);

    [Theory]
    [InlineData("log")]
    [InlineData("skip")]
    public async Task HttpBulk_HistoryAndChildren_ReadEachHabitSetOnce(string operation)
    {
        var counter = new CountingDbCommandInterceptor();
        using var database = new SqliteOrbitDbContextFactory(counter);
        var sample = await Run(database.Context, database.CreateContext, counter, operation, 10);
        output.WriteLine(JsonSerializer.Serialize(sample));
        sample.HabitRows.Should().Be(10);
    }

    [PostgresTheory]
    [InlineData("log", 5)]
    [InlineData("log", 10)]
    [InlineData("skip", 5)]
    [InlineData("skip", 10)]
    public Task Postgres_HttpBulk_PhaseTimings(string operation, int count) => PostgresTestDatabase.WithDatabase(async database =>
    {
        var counter = new CountingDbCommandInterceptor();
        OrbitDbContext Context() => new(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql(database.Database.GetConnectionString(), options => options.EnableRetryOnFailure(3))
            .AddInterceptors(counter).Options);
        var probe = new BulkRequestProbe();
        await using var host = new BulkHabitsPipelineHost(Context, probe, Today);
        var samples = new List<Sample>();
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var sample = await Run(database, Context, counter, operation, count, host, probe);
            output.WriteLine(JsonSerializer.Serialize(sample));
            if (iteration > 0) samples.Add(sample);
        }
        var median = samples.OrderBy(sample => sample.TotalMs).ElementAt(samples.Count / 2);
        output.WriteLine($"p50 {operation} {count}: {JsonSerializer.Serialize(median)}");
    });

    private static async Task<Sample> Run(OrbitDbContext database, Func<OrbitDbContext> context,
        CountingDbCommandInterceptor counter, string operation, int count,
        BulkHabitsPipelineHost? existingHost = null, BulkRequestProbe? existingProbe = null)
    {
        var user = User.Create("Bulk user", $"bulk-{Guid.NewGuid():N}@example.test").Value;
        user.SetStripeSubscription("bulk-local", new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        database.Users.Add(user);
        var parent = CreateHabit(user.Id, "Parent", true);
        var children = Enumerable.Range(0, 4).Select(index => CreateHabit(user.Id, $"Child {index}", true, parent.Id)).ToArray();
        var habits = children.Concat([parent]).Concat(Enumerable.Range(0, count - 5)
            .Select(index => CreateHabit(user.Id, $"Habit {index}", false))).ToArray();
        foreach (var habit in habits)
        {
            for (var offset = 400; offset > 0; offset--)
                habit.Log(Today.AddDays(-offset), advanceDueDate: false, loggedOnLocalDate: Today.AddDays(-offset)).IsSuccess.Should().BeTrue();
        }
        database.Habits.AddRange(habits);
        foreach (var habit in habits)
        {
            database.Notifications.Add(Notification.Create(user.Id, "Reminder", "Due", habitId: habit.Id,
                dedupeKey: HabitReminderInbox.Key(habit.Id, Today)));
            database.Notifications.Add(Notification.Create(user.Id, "Other reminder", "Due", habitId: habit.Id,
                dedupeKey: HabitReminderInbox.Key(habit.Id, Today.AddDays(-2))));
        }
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();
        var probe = existingProbe ?? new BulkRequestProbe();
        probe.Milliseconds.Clear();
        await using var ownedHost = existingHost is null ? new BulkHabitsPipelineHost(context, probe, Today) : null;
        var host = existingHost ?? ownedHost!;
        var body = JsonSerializer.Serialize(new { items = habits.Select(habit => new { habitId = habit.Id }) });
        counter.Reset();
        var start = Stopwatch.GetTimestamp();
        var key = Guid.NewGuid().ToString();
        var response = await host.SendAsync(user.Id, operation, body, key);
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        response.Response.StatusCode.Should().Be(200);
        response.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(response.Response.Body);
        var results = json.RootElement.GetProperty("results").EnumerateArray().ToArray();
        results.Should().HaveCount(count).And.OnlyContain(result => result.GetProperty("status").GetString() == "Success");
        var phases = new Dictionary<string, double>(probe.Milliseconds);
        phases["processing and HTTP"] = elapsed - phases.Values.Sum();
        var sample = new Sample(elapsed, phases, counter.CommandCount, counter.Commands.Count(IsHabitRead),
            counter.Commands.Where(IsHabitRead).Sum(command => command.Rows),
            counter.Commands.Sum(command => command.Rows));
        if (operation == "log")
        {
            (await database.HabitLogs.CountAsync(log => habits.Select(habit => habit.Id).Contains(log.HabitId) && log.Date == Today))
                .Should().Be(count);
            if (count > 5) (await database.XpAwardLogs.AnyAsync(award => award.UserId == user.Id)).Should().BeTrue();
        }
        else
            (await database.Habits.Where(habit => habit.UserId == user.Id).AllAsync(habit => habit.NextDueDate > Today)).Should().BeTrue();
        var reminders = await database.Notifications.Where(notification => notification.UserId == user.Id
            && notification.DedupeKey != null && notification.DedupeKey.StartsWith("habit-reminder:")).ToListAsync();
        var expectedRead = operation == "log" ? results.Count(result => result.GetProperty("logId").ValueKind != JsonValueKind.Null) : count;
        reminders.Count(reminder => reminder.IsRead).Should().Be(expectedRead);
        counter.Reset();
        var replay = await host.SendAsync(user.Id, operation, body, key);
        replay.Response.StatusCode.Should().Be(200);
        replay.Response.Body.Position = 0;
        using var replayJson = await JsonDocument.ParseAsync(replay.Response.Body);
        replayJson.RootElement.GetRawText().Should().Be(json.RootElement.GetRawText());
        counter.CommandCount.Should().Be(1);
        counter.WriteCommandCount.Should().Be(0);
        return sample;
    }

    private static Habit CreateHabit(Guid userId, string title, bool bad, Guid? parentId = null) =>
        Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, userId, title, FrequencyUnit.Day, 1,
            Today.AddDays(-500), IsBadHabit: bad, ParentHabitId: parentId)).Value;

    private static bool IsHabitRead(CapturedDbCommand command) => command.Sql.Contains("\"Title\"", StringComparison.Ordinal)
        && command.Sql.Contains("FROM \"Habits\"", StringComparison.Ordinal)
        && !command.Sql.Contains("JOIN", StringComparison.Ordinal);

    private sealed record Sample(double TotalMs, Dictionary<string, double> PhasesMs, int Commands, int HabitReads, int HabitRows, int Rows);
}
