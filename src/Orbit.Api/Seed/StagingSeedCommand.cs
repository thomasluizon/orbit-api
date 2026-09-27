using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Orbit.Application.Habits.Services;
using Orbit.Application.Tags.Commands;
using Orbit.Application.Tags.Validators;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Api.Seed;

public static class StagingSeedCommand
{
    public static async Task RunAsync(IConfiguration configuration, string environment)
    {
        var email = configuration["Seed:OwnerEmail"];
        var connectionString = NormalizeConnectionString(configuration["Seed:DatabaseUrl"]
            ?? configuration.GetConnectionString("DefaultConnection"));
        var expectedHost = configuration["Seed:ExpectedHost"];
        ValidateTarget(environment, connectionString, expectedHost, email);

        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using var db = new OrbitDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var timezone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timezone));
        await new StagingSeedService(db).SeedAsync(email!, today);
        await transaction.CommitAsync();
    }

    public static void ValidateTarget(string environment, string? connectionString, string? expectedHost, string? ownerEmail)
    {
        if (!string.Equals(environment, "Staging", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Staging seed requires the Staging environment.");
        if (string.IsNullOrWhiteSpace(ownerEmail))
            throw new InvalidOperationException("Seed:OwnerEmail is required.");
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(expectedHost))
            throw new InvalidOperationException("The staging connection and Seed:ExpectedHost are required.");

        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.Equals(connection.Host, expectedHost, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(connection.Database, "orbit_staging", StringComparison.Ordinal)
            || !string.Equals(connection.Username, "orbit_staging", StringComparison.Ordinal))
            throw new InvalidOperationException("Seed target is not the configured staging database.");
    }

    public static string? NormalizeConnectionString(string? source)
    {
        if (source is null || !(source.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)))
            return source;

        var uri = new Uri(source);
        var credentials = uri.UserInfo.Split(':', 2);
        if (credentials.Length != 2)
            throw new InvalidOperationException("Database URL has no password.");
        return new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(credentials[0]),
            Password = Uri.UnescapeDataString(credentials[1]),
            SslMode = SslMode.Require
        }.ConnectionString;
    }
}

public sealed class StagingSeedService(OrbitDbContext db)
{
    private sealed record HabitSpec(
        string Title,
        FrequencyUnit? Unit,
        int? Quantity,
        bool IsBad = false,
        bool IsGeneral = false,
        bool IsFlexible = false,
        IReadOnlyList<DayOfWeek>? Days = null,
        IReadOnlyList<ChecklistItem>? Checklist = null,
        string? Parent = null);

    private static readonly HabitSpec[] Habits =
    [
        new("Drink water", FrequencyUnit.Day, 1),
        new("Morning walk", FrequencyUnit.Day, 1, Days: [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday]),
        new("Read for 20 minutes", FrequencyUnit.Day, 1),
        new("Weekly review", FrequencyUnit.Week, 1),
        new("Review budget", FrequencyUnit.Month, 1),
        new("Annual health review", FrequencyUnit.Year, 1),
        new("Exercise three times", FrequencyUnit.Week, 3, IsFlexible: true),
        new("Submit a document", null, null),
        new("Avoid sugary drinks", FrequencyUnit.Day, 1, IsBad: true),
        new("Capture ideas", null, null, IsGeneral: true),
        new("Morning routine", FrequencyUnit.Day, 1, Checklist: [new ChecklistItem("Prepare breakfast", false), new ChecklistItem("Plan the day", false)]),
        new("Stretch for five minutes", FrequencyUnit.Day, 1, Parent: "Morning routine")
    ];

    public async Task SeedAsync(string ownerEmail, DateOnly today, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = ownerEmail.Trim().ToLowerInvariant();
        var user = await db.Users.IgnoreQueryFilters().SingleOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);
        if (user is null)
        {
            user = Require(User.Create("Staging Owner", normalizedEmail));
            user.SetLanguage("en");
            user.SeedDefaultHandle();
            Require(user.SetTimeZone("America/Sao_Paulo"));
            db.Users.Add(user);
            await db.SaveChangesAsync(cancellationToken);
        }

        var tags = await SeedTagsAsync(user.Id, cancellationToken);

        var existing = await db.Habits.Include(h => h.Logs).Include(h => h.Tags)
            .Where(h => h.UserId == user.Id).ToListAsync(cancellationToken);
        var byTitle = existing.ToDictionary(h => h.Title, StringComparer.Ordinal);
        foreach (var spec in Habits)
        {
            if (byTitle.ContainsKey(spec.Title)) continue;
            var anchor = InitialDate(spec, today);
            if (spec.Days is { Count: > 0 })
                while (!spec.Days.Contains(anchor.DayOfWeek)) anchor = anchor.AddDays(1);
            var parentId = spec.Parent is null ? (Guid?)null : byTitle[spec.Parent].Id;
            var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
            var createdAtUtc = TimeZoneInfo.ConvertTimeToUtc(anchor.ToDateTime(TimeOnly.MinValue), zone);
            var habit = Require(Habit.Create(new HabitCreateParams(
                user.Id, spec.Title, spec.Unit, spec.Quantity, anchor,
                Days: spec.Days, IsBadHabit: spec.IsBad, ParentHabitId: parentId,
                ChecklistItems: spec.Checklist, IsGeneral: spec.IsGeneral, IsFlexible: spec.IsFlexible,
                CreatedAtUtc: createdAtUtc)));
            if (spec.Title is "Drink water" or "Morning walk" or "Exercise three times") habit.AddTag(tags.Single(t => t.Name == "Health"));
            if (spec.Title is "Read for 20 minutes") habit.AddTag(tags.Single(t => t.Name == "Learning"));
            if (spec.Title is "Weekly review" or "Review budget") habit.AddTag(tags.Single(t => t.Name == "Planning"));
            db.Habits.Add(habit);
            byTitle.Add(spec.Title, habit);
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var habit in byTitle.Values)
        {
            for (var offset = 29; offset >= 0; offset--)
            {
                var date = today.AddDays(-offset);
                if (habit.FrequencyUnit is null && !habit.IsGeneral) continue;
                if (!habit.IsGeneral && !HabitScheduleService.IsHabitDueOnDate(habit, date)) continue;
                if (offset > 0 && (date.DayNumber + habit.Title.Length) % 6 == 0) continue;
                if (habit.IsBadHabit && offset % 11 != 0) continue;
                if (habit.IsGeneral && offset % 3 != 0) continue;
                if (habit.IsFlexible && date.DayOfWeek is not (DayOfWeek.Monday or DayOfWeek.Wednesday or DayOfWeek.Friday)) continue;
                if (habit.Logs.Any(log => log.Date == date)) continue;
                db.HabitLogs.Add(Require(habit.Log(date, advanceDueDate: false)));
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        var goals = await db.Goals.Include(g => g.Habits).Where(g => g.UserId == user.Id).ToListAsync(cancellationToken);
        AddGoal("Read consistently", 30, GoalType.Standard, ["Read for 20 minutes"], goals, byTitle, user.Id, today);
        AddGoal("Build a hydration streak", 14, GoalType.Streak, ["Drink water"], goals, byTitle, user.Id, today);

        await db.SaveChangesAsync(cancellationToken);
    }

    private static DateOnly InitialDate(HabitSpec spec, DateOnly today) =>
        spec.Unit is null && !spec.IsGeneral ? today : today.AddDays(-29);

    private async Task<List<Tag>> SeedTagsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var tags = await db.Tags.Where(t => t.UserId == userId).ToListAsync(cancellationToken);
        using var tagCache = new MemoryCache(new MemoryCacheOptions());
        var createTag = new CreateTagCommandHandler(
            new GenericRepository<Tag>(db), new UnitOfWork(db, new DatabaseConnectionSettings()), tagCache);
        var tagValidator = new CreateTagCommandValidator();
        foreach (var (name, color) in new[] { ("Health", "#43A047"), ("Learning", "#5C6BC0"), ("Planning", "#FB8C00") })
        {
            if (tags.Any(t => t.Name == name)) continue;
            var command = new CreateTagCommand(userId, name, color);
            var validation = await tagValidator.ValidateAsync(command, cancellationToken);
            if (!validation.IsValid) throw new InvalidOperationException(validation.ToString());
            var tagId = Require(await createTag.Handle(command, cancellationToken));
            tags.Add(db.Tags.Local.Single(t => t.Id == tagId));
        }
        return tags;
    }

    private void AddGoal(string title, int target, GoalType type, string[] habitTitles,
        List<Goal> goals, Dictionary<string, Habit> habits, Guid userId, DateOnly today)
    {
        if (goals.Any(g => g.Title == title)) return;
        var goal = Require(Goal.Create(new Goal.CreateGoalParams(userId, title, target, "completions", Type: type)));
        foreach (var habitTitle in habitTitles) goal.AddHabit(habits[habitTitle]);
        if (type == GoalType.Standard)
            Require(goal.SyncStandardProgress(goal.Habits.Sum(h => h.Logs.Count(l => l.Value > 0))));
        else
            Require(goal.SyncStreakProgress(HabitMetricsCalculator.Calculate(
                habits[habitTitles[0]], today, 1,
                TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")).CurrentStreak));
        db.Goals.Add(goal);
        goals.Add(goal);
    }

    private static T Require<T>(Orbit.Domain.Common.Result<T> result) =>
        result.IsSuccess ? result.Value : throw new InvalidOperationException(result.Error);

    private static void Require(Orbit.Domain.Common.Result result)
    {
        if (result.IsFailure) throw new InvalidOperationException(result.Error);
    }
}
