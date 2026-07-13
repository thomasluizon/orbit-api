using System.Data.Common;
using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the FormatDeadlineBody logic, NotifyDaysBefore constants, and the compute-only
/// streak refresh that runs before deadline notifications fire (so bodies show live progress
/// and goals already at target are excluded) without persisting the streak or completing the
/// goal. The background service loop itself is an integration concern.
/// </summary>
public class GoalDeadlineNotificationServiceTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static Goal CreateGoal(
        decimal currentValue = 5,
        decimal targetValue = 10,
        string unit = "km",
        DateOnly? deadline = null)
    {
        return Goal.Create(new Goal.CreateGoalParams(
            ValidUserId,
            "Run Marathon",
            targetValue,
            unit,
            Deadline: deadline ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7))).Value;
    }

    [Fact]
    public void FormatDeadlineBody_OneDayBefore_English_ReturnsTomorrowMessage()
    {
        var goal = CreateGoal();
        goal.UpdateProgress(5);

        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, goal.CurrentValue,1, "en");

        body.Should().Contain("due tomorrow");
        body.Should().Contain("5/10 km");
    }

    [Fact]
    public void FormatDeadlineBody_OneDayBefore_Portuguese_ReturnsTomorrowMessage()
    {
        var goal = CreateGoal();
        goal.UpdateProgress(3);

        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, goal.CurrentValue,1, "pt-br");

        body.Should().Contain("amanhã");
        body.Should().Contain("3/10 km");
    }

    [Fact]
    public void FormatDeadlineBody_ThreeDaysBefore_English_ReturnsDaysMessage()
    {
        var goal = CreateGoal();

        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, goal.CurrentValue,3, "en");

        body.Should().Contain("due in 3 days");
    }

    [Fact]
    public void FormatDeadlineBody_SevenDaysBefore_English_ReturnsDaysMessage()
    {
        var goal = CreateGoal();

        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, goal.CurrentValue,7, "en");

        body.Should().Contain("due in 7 days");
    }

    [Fact]
    public void FormatDeadlineBody_ThreeDaysBefore_Portuguese_ReturnsDaysMessage()
    {
        var goal = CreateGoal();

        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, goal.CurrentValue,3, "pt-br");

        body.Should().Contain("termina em 3 dias");
    }

    [Fact]
    public void FormatDeadlineBody_SevenDaysBefore_Portuguese_ReturnsDaysMessage()
    {
        var goal = CreateGoal();

        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, goal.CurrentValue,7, "pt");

        body.Should().Contain("termina em 7 dias");
    }

    [Fact]
    public void FormatDeadlineBody_IncludesProgressText()
    {
        var goal = CreateGoal(targetValue: 100, unit: "pages");
        goal.UpdateProgress(42);

        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, goal.CurrentValue,3, "en");

        body.Should().Contain("42/100 pages");
    }

    [Fact]
    public void FormatDeadlineBody_ZeroProgress_ShowsZero()
    {
        var goal = CreateGoal(targetValue: 50, unit: "sessions");

        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, goal.CurrentValue,1, "en");

        body.Should().Contain("0/50 sessions");
    }

    [Theory]
    [InlineData(2, "en", "due in 2 days")]
    [InlineData(5, "en", "due in 5 days")]
    [InlineData(14, "en", "due in 14 days")]
    public void FormatDeadlineBody_VariousDays_English_FormatsCorrectly(int days, string lang, string expected)
    {
        var goal = CreateGoal();
        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, goal.CurrentValue,days, lang);
        body.Should().Contain(expected);
    }

    [Theory]
    [InlineData(2, "pt-br", "termina em 2 dias")]
    [InlineData(5, "pt", "termina em 5 dias")]
    [InlineData(14, "pt-br", "termina em 14 dias")]
    public void FormatDeadlineBody_VariousDays_Portuguese_FormatsCorrectly(int days, string lang, string expected)
    {
        var goal = CreateGoal();
        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, goal.CurrentValue,days, lang);
        body.Should().Contain(expected);
    }

    [Fact]
    public void FormatDeadlineBody_OneDayBefore_English_DoesNotContainDaysPlural()
    {
        var goal = CreateGoal();
        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, goal.CurrentValue,1, "en");

        body.Should().Contain("tomorrow");
        body.Should().NotContain("in 1 days");
    }

    [Fact]
    public void FormatDeadlineBody_OneDayBefore_Portuguese_DoesNotContainDiasPlural()
    {
        var goal = CreateGoal();
        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, goal.CurrentValue,1, "pt-br");

        body.Should().Contain("amanhã");
        body.Should().NotContain("em 1 dias");
    }

    [Fact]
    public void FormatDeadlineBody_DecimalProgress_FormatsCorrectly()
    {
        var goal = CreateGoal(targetValue: 10, unit: "miles");
        goal.UpdateProgress(3.5m);

        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, goal.CurrentValue,3, "en");

        body.Should().Contain("3.5/10 miles");
    }

    [Fact]
    public void FormatDeadlineBody_LargeTargetValue_FormatsCorrectly()
    {
        var goal = CreateGoal(targetValue: 10000, unit: "steps");
        goal.UpdateProgress(5000);

        var body = GoalDeadlineNotificationService.FormatDeadlineBody(goal, goal.CurrentValue,7, "en");

        body.Should().Contain("5000/10000 steps");
    }

    [Fact]
    public void NotifyDaysBefore_ContainsExpectedValues()
    {
        var field = typeof(GoalDeadlineNotificationService)
            .GetField("NotifyDaysBefore",
                BindingFlags.NonPublic | BindingFlags.Static)!;
        var values = (int[])field.GetValue(null)!;

        values.Should().BeEquivalentTo([7, 3, 1]);
    }

    [Fact]
    public void NotifyDaysBefore_IsSortedDescending()
    {
        var field = typeof(GoalDeadlineNotificationService)
            .GetField("NotifyDaysBefore",
                BindingFlags.NonPublic | BindingFlags.Static)!;
        var values = (int[])field.GetValue(null)!;

        values.Should().BeInDescendingOrder();
    }

    [Fact]
    public void DeduplicationKey_MatchesExpectedFormat()
    {
        var goalId = Guid.NewGuid();
        var daysBefore = 3;

        var key = $"goal-deadline-{goalId}-{daysBefore}d";

        key.Should().StartWith("goal-deadline-");
        key.Should().EndWith("3d");
        key.Should().Contain(goalId.ToString());
    }

    [Fact]
    public void DeduplicationKey_DifferentGoalsSameDay_ProduceDifferentKeys()
    {
        var goal1 = Guid.NewGuid();
        var goal2 = Guid.NewGuid();

        var key1 = $"goal-deadline-{goal1}-3d";
        var key2 = $"goal-deadline-{goal2}-3d";

        key1.Should().NotBe(key2);
    }

    [Fact]
    public void DeduplicationKey_SameGoalDifferentDays_ProduceDifferentKeys()
    {
        var goalId = Guid.NewGuid();

        var key7 = $"goal-deadline-{goalId}-7d";
        var key3 = $"goal-deadline-{goalId}-3d";
        var key1 = $"goal-deadline-{goalId}-1d";

        key7.Should().NotBe(key3);
        key3.Should().NotBe(key1);
    }

    [Fact]
    public async Task CheckAndSendDeadlineNotifications_BadHabitStreakGoal_NotifiesWithFreshProgressWithoutPersisting()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var user = User.Create("Thomas", "thomas@test.com").Value;

        var goal = Goal.Create(new Goal.CreateGoalParams(
            user.Id, "Avoid doom scrolling", 7, "days",
            Deadline: Today.AddDays(1), Type: GoalType.Streak)).Value;
        var badHabit = CreateBadHabitDueToday(user.Id);
        goal.AddHabit(badHabit);

        dbContext.Users.Add(user);
        dbContext.Habits.Add(badHabit);
        dbContext.Goals.Add(goal);
        await dbContext.SaveChangesAsync();

        string? capturedBody = null;
        pushService
            .SendToUserAsync(user.Id, Arg.Any<string>(), Arg.Do<string>(b => capturedBody = b), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var service = CreateService(dbContext, pushService);
        await service.CheckAndSendDeadlineNotifications(CancellationToken.None);

        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain("2/7 days");

        var reloaded = await dbContext.Goals.AsNoTracking().SingleAsync(g => g.Id == goal.Id);
        reloaded.CurrentValue.Should().Be(0);
        reloaded.Status.Should().Be(GoalStatus.Active);
    }

    [Fact]
    public async Task CheckAndSendDeadlineNotifications_StreakGoalFreshlyAtTarget_SendsNoNotificationAndDoesNotComplete()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var user = User.Create("Thomas", "thomas@test.com").Value;

        var goal = Goal.Create(new Goal.CreateGoalParams(
            user.Id, "Avoid doom scrolling", 2, "days",
            Deadline: Today.AddDays(1), Type: GoalType.Streak)).Value;
        var badHabit = CreateBadHabitDueToday(user.Id);
        goal.AddHabit(badHabit);

        dbContext.Users.Add(user);
        dbContext.Habits.Add(badHabit);
        dbContext.Goals.Add(goal);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService);
        await service.CheckAndSendDeadlineNotifications(CancellationToken.None);

        await pushService.DidNotReceive().SendToUserAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        var reloaded = await dbContext.Goals.AsNoTracking().SingleAsync(g => g.Id == goal.Id);
        reloaded.Status.Should().Be(GoalStatus.Active);
    }

    [Fact]
    public async Task CheckAndSendDeadlineNotifications_ExactSevenDays_SendsSevenDayMilestone()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var (user, goal) = await SeedDeadlineGoalAsync(dbContext, deadlineInDays: 7);

        var service = CreateService(dbContext, pushService);
        await service.CheckAndSendDeadlineNotifications(CancellationToken.None);

        await pushService.Received(1).SendToUserAsync(
            user.Id, Arg.Any<string>(), Arg.Is<string>(b => b.Contains("7 days")), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        (await dbContext.Notifications.CountAsync(n => n.Url == $"goal-deadline-{goal.Id}-7d")).Should().Be(1);
    }

    [Fact]
    public async Task CheckAndSendDeadlineNotifications_MissedSevenDayTick_StillSendsHighestCrossedMilestone()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var (user, goal) = await SeedDeadlineGoalAsync(dbContext, deadlineInDays: 5);

        var service = CreateService(dbContext, pushService);
        await service.CheckAndSendDeadlineNotifications(CancellationToken.None);

        await pushService.Received(1).SendToUserAsync(
            user.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        (await dbContext.Notifications.CountAsync(n => n.Url == $"goal-deadline-{goal.Id}-7d")).Should().Be(1);
        (await dbContext.Notifications.CountAsync(n => n.Url == $"goal-deadline-{goal.Id}-3d")).Should().Be(0);
    }

    [Fact]
    public async Task CheckAndSendDeadlineNotifications_GoalCreatedInsideThreeDayWindow_SendsThreeDayMilestone()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var (_, goal) = await SeedDeadlineGoalAsync(dbContext, deadlineInDays: 2);

        var service = CreateService(dbContext, pushService);
        await service.CheckAndSendDeadlineNotifications(CancellationToken.None);

        (await dbContext.Notifications.CountAsync(n => n.Url == $"goal-deadline-{goal.Id}-7d")).Should().Be(1);
    }

    [Fact]
    public async Task CheckAndSendDeadlineNotifications_AlreadySentSevenDay_DoesNotResend()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var (user, goal) = await SeedDeadlineGoalAsync(dbContext, deadlineInDays: 5);
        dbContext.Notifications.Add(Notification.Create(
            user.Id, goal.Title, "already sent", $"goal-deadline-{goal.Id}-7d"));
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService);
        await service.CheckAndSendDeadlineNotifications(CancellationToken.None);

        await pushService.DidNotReceive().SendToUserAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        (await dbContext.Notifications.CountAsync(n => n.Url == $"goal-deadline-{goal.Id}-7d")).Should().Be(1);
    }

    [Fact]
    public async Task CheckAndSendDeadlineNotifications_DeadlineToday_SendsNothing()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        await SeedDeadlineGoalAsync(dbContext, deadlineInDays: 0);

        var service = CreateService(dbContext, pushService);
        await service.CheckAndSendDeadlineNotifications(CancellationToken.None);

        await pushService.DidNotReceive().SendToUserAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndSendDeadlineNotifications_PushFiresOnlyAfterNotificationPersisted()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var (user, goal) = await SeedDeadlineGoalAsync(dbContext, deadlineInDays: 7);
        var dedupKey = $"goal-deadline-{goal.Id}-7d";

        var notificationExistedAtPushTime = false;
        pushService
            .SendToUserAsync(user.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                notificationExistedAtPushTime = dbContext.Notifications
                    .AsNoTracking().Any(n => n.Url == dedupKey);
                return Task.CompletedTask;
            });

        var service = CreateService(dbContext, pushService);
        await service.CheckAndSendDeadlineNotifications(CancellationToken.None);

        notificationExistedAtPushTime.Should().BeTrue();
        (await dbContext.Notifications.CountAsync(n => n.Url == dedupKey)).Should().Be(1);
    }

    [Fact]
    public async Task CheckAndSendDeadlineNotifications_OneGoalPersistFails_DoesNotSendItButStillSendsOthers()
    {
        var conflictUser = User.Create("Conflict", "conflict@test.com").Value;
        var healthyUser = User.Create("Healthy", "healthy@test.com").Value;
        var conflictGoal = Goal.Create(new Goal.CreateGoalParams(
            conflictUser.Id, "Run Marathon", 10, "km", Deadline: Today.AddDays(7))).Value;
        var healthyGoal = Goal.Create(new Goal.CreateGoalParams(
            healthyUser.Id, "Read Books", 10, "books", Deadline: Today.AddDays(7))).Value;
        var conflictKey = $"goal-deadline-{conflictGoal.Id}-7d";

        var interceptor = new ThrowUniqueViolationForUrlInterceptor(conflictKey);
        await using var dbContext = CreateInterceptingDbContext(interceptor);
        var pushService = Substitute.For<IPushNotificationService>();

        dbContext.Users.AddRange(conflictUser, healthyUser);
        dbContext.Goals.AddRange(conflictGoal, healthyGoal);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService);
        await service.CheckAndSendDeadlineNotifications(CancellationToken.None);

        await pushService.DidNotReceive().SendToUserAsync(
            conflictUser.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await pushService.Received(1).SendToUserAsync(
            healthyUser.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        (await dbContext.Notifications.CountAsync(n => n.Url == conflictKey)).Should().Be(0);
        (await dbContext.Notifications.CountAsync(n => n.Url == $"goal-deadline-{healthyGoal.Id}-7d")).Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_HostedLifecycle_RunsOneDeadlinePassThenStopsGracefully()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var (user, goal) = await SeedDeadlineGoalAsync(dbContext, deadlineInDays: 7);

        var firstPush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pushService
            .SendToUserAsync(user.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { firstPush.TrySetResult(); return Task.CompletedTask; });

        var service = CreateService(dbContext, pushService);

        await service.StartAsync(CancellationToken.None);
        await firstPush.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await service.StopAsync(CancellationToken.None);

        await pushService.Received(1).SendToUserAsync(
            user.Id, Arg.Any<string>(), Arg.Is<string>(b => b.Contains("7 days")), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        (await dbContext.Notifications.CountAsync(n => n.Url == $"goal-deadline-{goal.Id}-7d")).Should().Be(1);
        service.ExecuteTask.Should().NotBeNull();
        service.ExecuteTask!.IsCompleted.Should().BeTrue();
        service.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAndSendDeadlineNotifications_OneGoalPushThrows_StillNotifiesRemainingGoals()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var (throwingUser, throwingGoal) = await SeedDeadlineGoalAsync(
            dbContext, deadlineInDays: 7, name: "Alice", email: "alice@test.com");
        var (healthyUser, healthyGoal) = await SeedDeadlineGoalAsync(
            dbContext, deadlineInDays: 7, name: "Bob", email: "bob@test.com");

        pushService
            .SendToUserAsync(throwingUser.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("push transport down")));

        var service = CreateService(dbContext, pushService);

        var run = async () => await service.CheckAndSendDeadlineNotifications(CancellationToken.None);
        await run.Should().NotThrowAsync();

        await pushService.Received(1).SendToUserAsync(
            throwingUser.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await pushService.Received(1).SendToUserAsync(
            healthyUser.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        (await dbContext.Notifications.CountAsync(n => n.Url == $"goal-deadline-{healthyGoal.Id}-7d")).Should().Be(1);
        (await dbContext.Notifications.CountAsync(n => n.Url == $"goal-deadline-{throwingGoal.Id}-7d")).Should().Be(1);
    }

    private static async Task<(User User, Goal Goal)> SeedDeadlineGoalAsync(
        OrbitDbContext dbContext, int deadlineInDays, string name = "Thomas", string email = "thomas@test.com")
    {
        var user = User.Create(name, email).Value;
        var goal = Goal.Create(new Goal.CreateGoalParams(
            user.Id, "Run Marathon", 10, "km",
            Deadline: Today.AddDays(deadlineInDays))).Value;

        dbContext.Users.Add(user);
        dbContext.Goals.Add(goal);
        await dbContext.SaveChangesAsync();
        return (user, goal);
    }

    private static Habit CreateBadHabitDueToday(Guid userId)
    {
        var habit = Habit.Create(new HabitCreateParams(
            userId, "Doom scrolling", FrequencyUnit.Day, 1, IsBadHabit: true, DueDate: Today)).Value;
        SetCreatedAtUtc(habit, Today.AddDays(-1));
        return habit;
    }

    private static void SetCreatedAtUtc(Habit habit, DateOnly localDate)
    {
        typeof(Habit)
            .GetProperty(nameof(Habit.CreatedAtUtc))!
            .SetValue(habit, localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }

    private static OrbitDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"GoalDeadlineNotificationServiceTests_{Guid.NewGuid()}")
            .Options;
        return new OrbitDbContext(options);
    }

    private static OrbitDbContext CreateInterceptingDbContext(ISaveChangesInterceptor interceptor) =>
        new(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"GoalDeadlineNotificationServiceTests_{Guid.NewGuid()}")
            .AddInterceptors(interceptor)
            .Options);

    private static GoalDeadlineNotificationService CreateService(
        OrbitDbContext dbContext, IPushNotificationService pushService)
    {
        var serviceProvider = new ServiceCollection()
            .AddSingleton(dbContext)
            .AddSingleton(pushService)
            .BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        return new GoalDeadlineNotificationService(
            scopeFactory,
            NullLogger<GoalDeadlineNotificationService>.Instance,
            new ConfigurationBuilder().Build());
    }

    private sealed class ThrowUniqueViolationForUrlInterceptor(string conflictUrl) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var hasConflictingNotification = eventData.Context?.ChangeTracker
                .Entries<Notification>()
                .Any(e => e.State == EntityState.Added && e.Entity.Url == conflictUrl) == true;

            if (hasConflictingNotification)
                throw new DbUpdateException("duplicate", new FakeUniqueViolationException());

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class FakeUniqueViolationException : DbException
    {
        public override string SqlState => "23505";
    }
}
