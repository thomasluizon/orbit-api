using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
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
        var samples = new List<Sample>();
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var sample = await Run(database, Context, counter, operation, count);
            output.WriteLine(JsonSerializer.Serialize(sample));
            if (iteration > 0) samples.Add(sample);
        }
        var median = samples.OrderBy(sample => sample.TotalMs).ElementAt(samples.Count / 2);
        output.WriteLine($"p50 {operation} {count}: {JsonSerializer.Serialize(median)}");
    });

    private static async Task<Sample> Run(OrbitDbContext database, Func<OrbitDbContext> context,
        CountingDbCommandInterceptor counter, string operation, int count)
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
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();
        var probe = new BulkRequestProbe();
        await using var host = new BulkHabitsPipelineHost(context, probe, Today);
        var body = JsonSerializer.Serialize(new { items = habits.Select(habit => new { habitId = habit.Id }) });
        counter.Reset();
        var start = Stopwatch.GetTimestamp();
        var response = await host.SendAsync(user.Id, operation, body, Guid.NewGuid().ToString());
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        response.Response.StatusCode.Should().Be(200);
        response.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(response.Response.Body);
        var results = json.RootElement.GetProperty("results").EnumerateArray().ToArray();
        results.Should().HaveCount(count).And.OnlyContain(result => result.GetProperty("status").GetString() == "Success");
        if (operation == "log" && count > 5)
            (await database.XpAwardLogs.AnyAsync(award => award.UserId == user.Id)).Should().BeTrue();
        var phases = probe.Milliseconds;
        phases["processing and HTTP"] = elapsed - phases.Values.Sum();
        return new(elapsed, phases, counter.CommandCount, counter.Commands.Count(IsHabitRead),
            counter.Commands.Where(IsHabitRead).Sum(command => command.Rows),
            counter.Commands.Sum(command => command.Rows));
    }

    private static Habit CreateHabit(Guid userId, string title, bool bad, Guid? parentId = null) =>
        Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, userId, title, FrequencyUnit.Day, 1,
            Today.AddDays(-500), IsBadHabit: bad, ParentHabitId: parentId)).Value;

    private static bool IsHabitRead(CapturedDbCommand command) => command.Sql.Contains("\"Title\"", StringComparison.Ordinal)
        && command.Sql.Contains("FROM \"Habits\"", StringComparison.Ordinal)
        && !command.Sql.Contains("JOIN", StringComparison.Ordinal);

    private sealed record Sample(double TotalMs, Dictionary<string, double> PhasesMs, int Commands, int HabitReads, int HabitRows, int Rows);
}
