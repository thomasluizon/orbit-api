using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Gamification;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class FoundingAchievementReaderTests
{
    [Fact]
    public async Task ReadEvidenceAsync_CountsAboveOneWithReopenedGoal_ReturnsAllFiveInOneRoundTrip()
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var context = factory.Context;
        var user = User.Create("Founding User", "founding@example.com").Value;
        user.MarkFirstHabitCreated();
        user.MarkFirstHabitLogged();
        user.MarkAstraUsed();
        user.CompleteOnboardingChecklist();
        context.Users.Add(user);

        var habit = Habit.Create(new HabitCreateParams(
            user.Id,
            "Historical Habit",
            FrequencyUnit.Day,
            1,
            DueDate: new DateOnly(2026, 8, 1))).Value;
        habit.Log(new DateOnly(2026, 8, 1));
        habit.SoftDelete();
        context.Habits.Add(habit);
        var secondHabit = Habit.Create(new HabitCreateParams(
            user.Id,
            "Second Habit",
            FrequencyUnit.Day,
            1,
            DueDate: new DateOnly(2026, 8, 2))).Value;
        secondHabit.Log(new DateOnly(2026, 8, 2));
        context.Habits.Add(secondHabit);

        var goal = Goal.Create(user.Id, "Historical Goal", 1, "completion").Value;
        goal.MarkCompleted();
        goal.Reactivate();
        goal.SoftDelete();
        context.Goals.Add(goal);
        var secondGoal = Goal.Create(user.Id, "Second Goal", 1, "completion").Value;
        secondGoal.MarkCompleted();
        context.Goals.Add(secondGoal);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        counter.Reset();

        var evidence = await new FoundingAchievementReader(context).ReadEvidenceAsync(user.Id);

        evidence.Should().Be(new Orbit.Domain.Interfaces.FoundingAchievementEvidence(
            HasHabitLog: true,
            HasTopLevelHabit: true,
            HasGoal: true,
            HasCompletedGoal: true,
            HasCompletedOnboardingChecklist: true));
        counter.CommandCount.Should().Be(1);
    }

    [Fact]
    public async Task ReadEvidenceAsync_GoalCompletionAwardLog_SurvivesMissingGoal()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        var user = User.Create("Award User", "award@example.com").Value;
        context.Users.Add(user);
        context.XpAwardLogs.Add(XpAwardLog.Create(
            user.Id,
            100,
            XpAwardSource.GoalCompleted,
            Guid.NewGuid(),
            DateTime.UtcNow));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var evidence = await new FoundingAchievementReader(context).ReadEvidenceAsync(user.Id);

        evidence.Should().NotBeNull();
        evidence!.HasCompletedGoal.Should().BeTrue();
    }

    [Fact]
    public async Task ReadEvidenceAndCandidates_SkipAndBadHabitLogs_DoNotQualifyLiftoff()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        var skippedUser = User.Create("Skipped User", "skipped@example.com").Value;
        var badHabitUser = User.Create("Bad Habit User", "bad-habit@example.com").Value;
        context.Users.AddRange(skippedUser, badHabitUser);

        var flexibleHabit = Habit.Create(new HabitCreateParams(
            skippedUser.Id,
            "Flexible Habit",
            FrequencyUnit.Day,
            1,
            DueDate: new DateOnly(2026, 8, 1),
            IsFlexible: true)).Value;
        flexibleHabit.SkipFlexible(new DateOnly(2026, 8, 1));
        context.Habits.Add(flexibleHabit);

        var badHabit = Habit.Create(new HabitCreateParams(
            badHabitUser.Id,
            "Bad Habit",
            FrequencyUnit.Day,
            1,
            DueDate: new DateOnly(2026, 8, 1),
            IsBadHabit: true)).Value;
        badHabit.Log(new DateOnly(2026, 8, 1));
        context.Habits.Add(badHabit);

        context.UserAchievements.AddRange(
            UserAchievement.Create(skippedUser.Id, AchievementDefinitions.FirstOrbit),
            UserAchievement.Create(badHabitUser.Id, AchievementDefinitions.FirstOrbit));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var reader = new FoundingAchievementReader(context);
        var skippedEvidence = await reader.ReadEvidenceAsync(skippedUser.Id);
        var badHabitEvidence = await reader.ReadEvidenceAsync(badHabitUser.Id);
        var candidates = await reader.ReadCandidatePageAsync(null, 100);

        skippedEvidence!.HasHabitLog.Should().BeFalse();
        badHabitEvidence!.HasHabitLog.Should().BeFalse();
        candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadCandidatePageAsync_SkipsCompleteDeactivatedAndResetAccounts()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        var eligible = User.Create("Eligible", "eligible@example.com").Value;
        var complete = User.Create("Complete", "complete@example.com").Value;
        var deactivated = User.Create("Deactivated", "deactivated@example.com").Value;
        var reset = User.Create("Reset", "reset@example.com").Value;
        deactivated.Deactivate(DateTime.UtcNow.AddDays(30));
        context.Users.AddRange(eligible, complete, deactivated, reset);

        foreach (var user in new[] { eligible, complete, deactivated })
        {
            var habit = Habit.Create(new HabitCreateParams(
                user.Id,
                $"Habit {user.Id}",
                FrequencyUnit.Day,
                1,
                DueDate: new DateOnly(2026, 8, 1))).Value;
            habit.Log(new DateOnly(2026, 8, 1));
            context.Habits.Add(habit);
        }

        var foundingIds = new[]
        {
            AchievementDefinitions.Liftoff,
            AchievementDefinitions.FirstOrbit,
            AchievementDefinitions.MissionControl,
            AchievementDefinitions.GoalCrusher,
            AchievementDefinitions.OnboardingComplete
        };
        context.UserAchievements.AddRange(
            foundingIds.Select(id => UserAchievement.Create(complete.Id, id)));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var candidates = await new FoundingAchievementReader(context)
            .ReadCandidatePageAsync(null, 100);

        candidates.Select(candidate => candidate.UserId).Should().Equal(eligible.Id);
    }

    [Fact]
    public void BuildCandidatePageQuery_WithCursor_TranslatesUuidKeysetForPostgres()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_only;Username=translation;Password=translation")
            .Options;
        using var context = new OrbitDbContext(options);
        var cursor = new Orbit.Domain.Interfaces.FoundingAchievementCursor(
            Guid.Parse("00000000-0000-0000-0000-000000000100"),
            new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));

        var sql = new FoundingAchievementReader(context)
            .BuildCandidatePageQuery(cursor, 100)
            .ToQueryString();

        sql.Should().Contain(
            "(u.\"Id\", u.\"CreatedAtUtc\") > (@cursor_UserId, @cursor_CreatedAtUtc)");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
    }
}
