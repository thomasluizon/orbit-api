using FluentAssertions;
using NSubstitute;
using Orbit.Application.Goals.Services;
using Orbit.Application.Profile.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Profile;

public class ExportUserDataQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<GoalProgressLog> _goalProgressLogRepo = Substitute.For<IGenericRepository<GoalProgressLog>>();
    private readonly IGenericRepository<Tag> _tagRepo = Substitute.For<IGenericRepository<Tag>>();
    private readonly IGenericRepository<UserFact> _userFactRepo = Substitute.For<IGenericRepository<UserFact>>();
    private readonly IGenericRepository<Notification> _notificationRepo = Substitute.For<IGenericRepository<Notification>>();
    private readonly IGenericRepository<ChecklistTemplate> _checklistTemplateRepo = Substitute.For<IGenericRepository<ChecklistTemplate>>();
    private readonly IGenericRepository<UserAchievement> _userAchievementRepo = Substitute.For<IGenericRepository<UserAchievement>>();
    private readonly IGenericRepository<StreakFreeze> _streakFreezeRepo = Substitute.For<IGenericRepository<StreakFreeze>>();
    private readonly IGenericRepository<Referral> _referralRepo = Substitute.For<IGenericRepository<Referral>>();
    private readonly IGenericRepository<ApiKey> _apiKeyRepo = Substitute.For<IGenericRepository<ApiKey>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IStreakGoalReadSyncer _streakGoalReadSyncer = Substitute.For<IStreakGoalReadSyncer>();
    private readonly ExportUserDataQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    public ExportUserDataQueryHandlerTests()
    {
        _handler = new ExportUserDataQueryHandler(
            _userRepo, _habitRepo, _habitLogRepo, _goalRepo, _goalProgressLogRepo, _tagRepo, _userFactRepo,
            _notificationRepo, _checklistTemplateRepo, _userAchievementRepo, _streakFreezeRepo, _referralRepo, _apiKeyRepo,
            _userDateService, _streakGoalReadSyncer);

        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _streakGoalReadSyncer.ComputeFreshValuesAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());

        ReturnsEmpty(_habitRepo);
        ReturnsEmpty(_habitLogRepo);
        ReturnsEmpty(_goalRepo);
        ReturnsEmpty(_goalProgressLogRepo);
        ReturnsEmpty(_tagRepo);
        ReturnsEmpty(_userFactRepo);
        ReturnsEmpty(_notificationRepo);
        ReturnsEmpty(_checklistTemplateRepo);
        ReturnsEmpty(_userAchievementRepo);
        ReturnsEmpty(_streakFreezeRepo);
        ReturnsEmpty(_referralRepo);
        ReturnsEmpty(_apiKeyRepo);
    }

    private static void ReturnsEmpty<T>(IGenericRepository<T> repository) where T : Orbit.Domain.Common.Entity
    {
        repository.FindAsync(
            Arg.Any<Expression<Func<T, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<T>().AsReadOnly());
    }

    private static void Returns<T>(IGenericRepository<T> repository, params T[] entities) where T : Orbit.Domain.Common.Entity
    {
        repository.FindAsync(
            Arg.Any<Expression<Func<T, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(entities.ToList().AsReadOnly());
    }

    private void ArrangeUser(User user)
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
    }

    private static User CreateTestUser() => User.Create("Test User", "test@example.com").Value;

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _handler.Handle(new ExportUserDataQuery(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_EmptyAccount_ReturnsAccountSettingsAndEmptyCollections()
    {
        var user = CreateTestUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        ArrangeUser(user);

        var result = await _handler.Handle(new ExportUserDataQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Account.Name.Should().Be("Test User");
        result.Value.Account.Email.Should().Be("test@example.com");
        result.Value.Account.Plan.Should().Be("free");
        result.Value.Settings.AiMemoryEnabled.Should().Be(user.AiMemoryEnabled);
        result.Value.Habits.Should().BeEmpty();
        result.Value.Goals.Should().BeEmpty();
        result.Value.Tags.Should().BeEmpty();
        result.Value.Facts.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ProUser_MapsProPlan()
    {
        var user = CreateTestUser();
        user.StartTrial(DateTime.UtcNow.AddDays(5));
        ArrangeUser(user);

        var result = await _handler.Handle(new ExportUserDataQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Account.Plan.Should().Be("pro");
    }

    [Fact]
    public async Task Handle_HabitsWithLogs_GroupsLogsPerHabitOrderedByDate()
    {
        ArrangeUser(CreateTestUser());

        var habitWithLogs = Habit.Create(new HabitCreateParams(
            UserId, "Meditate", FrequencyUnit.Day, 1, DueDate: new DateOnly(2026, 4, 1))).Value;
        var habitWithoutLogs = Habit.Create(new HabitCreateParams(
            UserId, "Stretch", FrequencyUnit.Day, 1, DueDate: new DateOnly(2026, 4, 1))).Value;
        Returns(_habitRepo, habitWithLogs, habitWithoutLogs);

        Returns(
            _habitLogRepo,
            HabitLog.Create(habitWithLogs.Id, new DateOnly(2026, 4, 3), 1),
            HabitLog.Create(habitWithLogs.Id, new DateOnly(2026, 4, 1), 1, "first"),
            HabitLog.Create(habitWithLogs.Id, new DateOnly(2026, 4, 2), 1));

        var result = await _handler.Handle(new ExportUserDataQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Habits.Should().HaveCount(2);

        var exportedWithLogs = result.Value.Habits.Single(h => h.Id == habitWithLogs.Id);
        exportedWithLogs.Title.Should().Be("Meditate");
        exportedWithLogs.FrequencyUnit.Should().Be("Day");
        exportedWithLogs.Logs.Select(l => l.Date).Should().Equal(
            new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 2), new DateOnly(2026, 4, 3));
        exportedWithLogs.Logs[0].Note.Should().Be("first");

        result.Value.Habits.Single(h => h.Id == habitWithoutLogs.Id).Logs.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_GoalsWithProgress_MapsProgressLogsPerGoal()
    {
        ArrangeUser(CreateTestUser());

        var goalWithProgress = Goal.Create(new Goal.CreateGoalParams(UserId, "Read books", 12, "books")).Value;
        var goalWithoutProgress = Goal.Create(new Goal.CreateGoalParams(UserId, "Save money", 100, "BRL")).Value;
        Returns(_goalRepo, goalWithProgress, goalWithoutProgress);

        Returns(
            _goalProgressLogRepo,
            GoalProgressLog.Create(goalWithProgress.Id, 0, 3, "good month"));

        var result = await _handler.Handle(new ExportUserDataQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Goals.Should().HaveCount(2);

        var exportedWithProgress = result.Value.Goals.Single(g => g.Id == goalWithProgress.Id);
        exportedWithProgress.Title.Should().Be("Read books");
        exportedWithProgress.Status.Should().Be("Active");
        exportedWithProgress.ProgressLogs.Should().ContainSingle();
        exportedWithProgress.ProgressLogs[0].Value.Should().Be(3);
        exportedWithProgress.ProgressLogs[0].PreviousValue.Should().Be(0);
        exportedWithProgress.ProgressLogs[0].Note.Should().Be("good month");

        result.Value.Goals.Single(g => g.Id == goalWithoutProgress.Id).ProgressLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_StreakGoal_ExportsFreshCurrentValue()
    {
        ArrangeUser(CreateTestUser());

        var streakGoal = Goal.Create(new Goal.CreateGoalParams(
            UserId, "Avoid doom scrolling", 7, "days", Type: GoalType.Streak)).Value;

        _streakGoalReadSyncer
            .ComputeFreshValuesAsync(UserId, Today, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [streakGoal.Id] = 4 });

        Returns(_goalRepo, streakGoal);

        var result = await _handler.Handle(new ExportUserDataQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _streakGoalReadSyncer.Received(1).ComputeFreshValuesAsync(UserId, Today, Arg.Any<CancellationToken>());
        result.Value.Goals.Should().ContainSingle();
        result.Value.Goals[0].CurrentValue.Should().Be(4);
    }

    [Fact]
    public async Task Handle_TagsAndFacts_Mapped()
    {
        ArrangeUser(CreateTestUser());

        var tag = Tag.Create(UserId, "Health", "#FF0000").Value;
        Returns(_tagRepo, tag);

        var fact = UserFact.Create(UserId, "Prefers morning workouts", "routine").Value;
        Returns(_userFactRepo, fact);

        var result = await _handler.Handle(new ExportUserDataQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Tags.Should().ContainSingle();
        result.Value.Tags[0].Name.Should().Be("Health");
        result.Value.Tags[0].Color.Should().Be("#FF0000");
        result.Value.Facts.Should().ContainSingle();
        result.Value.Facts[0].FactText.Should().Be("Prefers morning workouts");
        result.Value.Facts[0].Category.Should().Be("routine");
    }

    [Fact]
    public async Task Handle_NewCategories_AreExported()
    {
        ArrangeUser(CreateTestUser());

        var notification = Notification.Create(UserId, "Reminder", "Drink water", "/today");
        Returns(_notificationRepo, notification);

        var template = ChecklistTemplate.Create(UserId, "Morning", ["Water", "Stretch"]).Value;
        Returns(_checklistTemplateRepo, template);

        var achievement = UserAchievement.Create(UserId, "first_habit");
        Returns(_userAchievementRepo, achievement);

        var streakFreeze = StreakFreeze.Create(UserId, new DateOnly(2026, 4, 1));
        Returns(_streakFreezeRepo, streakFreeze);

        var referral = Referral.Create(UserId, Guid.NewGuid());
        Returns(_referralRepo, referral);

        var result = await _handler.Handle(new ExportUserDataQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Notifications.Should().ContainSingle();
        result.Value.Notifications[0].Title.Should().Be("Reminder");
        result.Value.Notifications[0].Url.Should().Be("/today");
        result.Value.ChecklistTemplates.Should().ContainSingle();
        result.Value.ChecklistTemplates[0].Items.Should().Equal("Water", "Stretch");
        result.Value.Achievements.Should().ContainSingle();
        result.Value.Achievements[0].AchievementId.Should().Be("first_habit");
        result.Value.StreakFreezes.Should().ContainSingle();
        result.Value.StreakFreezes[0].UsedOnDate.Should().Be(new DateOnly(2026, 4, 1));
        result.Value.Referrals.Should().ContainSingle();
        result.Value.Referrals[0].Status.Should().Be("Pending");
    }

    [Fact]
    public async Task Handle_OnlyExportsReferralsTheUserInitiated()
    {
        ArrangeUser(CreateTestUser());

        var outgoing = Referral.Create(UserId, Guid.NewGuid());
        _referralRepo
            .FindAsync(Arg.Any<Expression<Func<Referral, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var predicate = call.Arg<Expression<Func<Referral, bool>>>().Compile();
                var inbound = Referral.Create(Guid.NewGuid(), UserId);
                return new[] { outgoing, inbound }.Where(predicate).ToList().AsReadOnly();
            });

        var result = await _handler.Handle(new ExportUserDataQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Referrals.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_ApiKeys_ExportMetadataButNeverTheSecretHash()
    {
        ArrangeUser(CreateTestUser());

        var (apiKey, rawKey) = ApiKey.Create(UserId, "CI token", ["habits:read"]).Value;
        Returns(_apiKeyRepo, apiKey);

        var result = await _handler.Handle(new ExportUserDataQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ApiKeys.Should().ContainSingle();

        var exported = result.Value.ApiKeys[0];
        exported.Name.Should().Be("CI token");
        exported.KeyPrefix.Should().Be(apiKey.KeyPrefix);
        exported.Scopes.Should().Equal("habits:read");

        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        json.Should().NotContain(apiKey.KeyHash);
        json.Should().NotContain(rawKey);
        json.Should().NotContain("KeyHash");
    }

    [Fact]
    public async Task Handle_Subscription_ExportsOwnStateNotProviderSecrets()
    {
        var user = CreateTestUser();
        user.SetStripeCustomerId("cus_secret123");
        user.SetStripeSubscription("sub_secret456", DateTime.UtcNow.AddDays(30), SubscriptionInterval.Monthly);
        ArrangeUser(user);

        var result = await _handler.Handle(new ExportUserDataQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Subscription.Plan.Should().Be("Pro");
        result.Value.Subscription.Source.Should().Be("Stripe");
        result.Value.Subscription.Interval.Should().Be("Monthly");

        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        json.Should().NotContain("cus_secret123");
        json.Should().NotContain("sub_secret456");
        json.Should().NotContain("StripeCustomerId");
        json.Should().NotContain("PlayPurchaseToken");
    }
}
