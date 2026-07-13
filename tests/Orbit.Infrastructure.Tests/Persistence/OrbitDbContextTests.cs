using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public class OrbitDbContextTests
{
    [Fact]
    public void Model_WithoutEncryptionService_UsesValueConvertersForSerializedCollections()
    {
        using var context = CreateContext();

        var habit = context.Model.FindEntityType(typeof(Habit))!;
        var daysConverter = habit.FindProperty(nameof(Habit.Days))!.GetValueConverter();

        daysConverter.Should().NotBeNull();
        daysConverter!.ConvertToProvider(new List<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Wednesday })
            .Should().Be("[1,3]");
        habit.FindProperty(nameof(Habit.ChecklistItems))!.GetValueComparer().Should().NotBeNull();
        habit.FindProperty(nameof(Habit.ReminderTimes))!.GetValueComparer().Should().NotBeNull();
        habit.FindProperty(nameof(Habit.ScheduledReminders))!.GetValueComparer().Should().NotBeNull();
    }

    [Fact]
    public void Model_WithEncryptionService_ConfiguresEncryptedValueConverters()
    {
        var encryptionService = Substitute.For<IEncryptionService>();
        encryptionService.Encrypt(Arg.Any<string>()).Returns(call => call.Arg<string>());
        encryptionService.Decrypt(Arg.Any<string>()).Returns(call => call.Arg<string>());
        encryptionService.EncryptNullable(Arg.Any<string?>()).Returns(call => call.Arg<string?>());
        encryptionService.DecryptNullable(Arg.Any<string?>()).Returns(call => call.Arg<string?>());

        using var context = CreateContext(encryptionService);

        context.Model.FindEntityType(typeof(User))!.FindProperty(nameof(User.GoogleAccessToken))!.GetValueConverter().Should().NotBeNull();
        context.Model.FindEntityType(typeof(Habit))!.FindProperty(nameof(Habit.Title))!.GetValueConverter().Should().NotBeNull();
        context.Model.FindEntityType(typeof(Habit))!.FindProperty(nameof(Habit.Description))!.GetValueConverter().Should().NotBeNull();
        context.Model.FindEntityType(typeof(HabitLog))!.FindProperty(nameof(HabitLog.Note))!.GetValueConverter().Should().NotBeNull();
        context.Model.FindEntityType(typeof(UserFact))!.FindProperty(nameof(UserFact.FactText))!.GetValueConverter().Should().NotBeNull();
        context.Model.FindEntityType(typeof(Goal))!.FindProperty(nameof(Goal.Title))!.GetValueConverter().Should().NotBeNull();
        context.Model.FindEntityType(typeof(GoalProgressLog))!.FindProperty(nameof(GoalProgressLog.Note))!.GetValueConverter().Should().NotBeNull();
    }

    [Fact]
    public void Model_WithEncryptionService_WiresUserGoogleTokensToNullableEncryptingConverter()
    {
        var encryptionService = Substitute.For<IEncryptionService>();

        using var context = CreateContext(encryptionService);
        var user = context.Model.FindEntityType(typeof(User))!;

        user.FindProperty(nameof(User.GoogleAccessToken))!.GetValueConverter()
            .Should().BeOfType<NullableEncryptionValueConverter>();
        user.FindProperty(nameof(User.GoogleRefreshToken))!.GetValueConverter()
            .Should().BeOfType<NullableEncryptionValueConverter>();
    }

    [Fact]
    public void NullableEncryptingConverter_WithRealEncryption_ProducesCiphertextAtRestAndDecryptsOnRead()
    {
        var encryptionService = new EncryptionService(
            Options.Create(new EncryptionSettings { Key = "DdyUCjjdK326cB9lY00tyUvRDpCQcYJOJIpu21I1D8c=" }),
            NullLogger<EncryptionService>.Instance);
        var converter = new NullableEncryptionValueConverter(encryptionService);

        const string plaintext = "ya29.PLAINTEXT-GOOGLE-TOKEN";
        var atRest = (string?)converter.ConvertToProvider(plaintext);

        atRest.Should().StartWith("enc:").And.NotBe(plaintext);
        converter.ConvertFromProvider(atRest).Should().Be(plaintext);
        converter.ConvertToProvider(null).Should().BeNull();
    }

    [Fact]
    public void Model_WithPostgresProvider_UsesArrayAndJsonbColumnMetadata()
    {
        using var context = CreatePostgresContext();

        var habit = context.Model.FindEntityType(typeof(Habit))!;

        habit.FindProperty(nameof(Habit.Days))!.GetColumnType().Should().Be("text[]");
        habit.FindProperty(nameof(Habit.ChecklistItems))!.GetDefaultValueSql().Should().Be("'[]'::jsonb");
        habit.FindProperty(nameof(Habit.ReminderTimes))!.GetDefaultValueSql().Should().Be("'[15]'::jsonb");
        habit.FindProperty(nameof(Habit.ScheduledReminders))!.GetDefaultValueSql().Should().Be("'[]'::jsonb");
    }

    [Fact]
    public void Model_SentReminder_UniqueIndexIncludesReminderTimeUtcAndWhen()
    {
        using var context = CreateContext();

        var sentReminder = context.Model.FindEntityType(typeof(SentReminder))!;
        var uniqueIndex = sentReminder.GetIndexes().Single(i => i.IsUnique);

        uniqueIndex.Properties.Select(p => p.Name).Should().Equal(
            nameof(SentReminder.HabitId),
            nameof(SentReminder.Date),
            nameof(SentReminder.MinutesBefore),
            nameof(SentReminder.ReminderTimeUtc),
            nameof(SentReminder.When));
    }

    [Fact]
    public void Model_GoogleEventIdColumns_AreWidenedForLongCalendarEventIds()
    {
        using var context = CreateContext();

        var habitGoogleEventId = context.Model.FindEntityType(typeof(Habit))!
            .FindProperty(nameof(Habit.GoogleEventId))!;
        var suggestionGoogleEventId = context.Model.FindEntityType(typeof(GoogleCalendarSyncSuggestion))!
            .FindProperty(nameof(GoogleCalendarSyncSuggestion.GoogleEventId))!;

        habitGoogleEventId.GetMaxLength().Should().Be(1024);
        suggestionGoogleEventId.GetMaxLength().Should().Be(1024);
    }

    [Fact]
    public void Model_TagUniqueIndex_FiltersSoftDeletedRowsSoNamesCanBeReusedAfterDeletion()
    {
        using var context = CreatePostgresContext();

        var tag = context.Model.FindEntityType(typeof(Tag))!;
        var uniqueIndex = tag.GetIndexes().Single(i =>
            i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(Tag.UserId), nameof(Tag.Name) }));

        uniqueIndex.GetFilter().Should().Be("\"IsDeleted\" = FALSE");
    }

    [Fact]
    public void Model_UnboundedStringColumns_AreConstrainedToMaxLengths()
    {
        using var context = CreatePostgresContext();

        var tag = context.Model.FindEntityType(typeof(Tag))!;
        tag.FindProperty(nameof(Tag.Name))!.GetMaxLength().Should().Be(50);
        tag.FindProperty(nameof(Tag.Color))!.GetMaxLength().Should().Be(50);

        var user = context.Model.FindEntityType(typeof(User))!;
        user.FindProperty(nameof(User.ColorScheme))!.GetMaxLength().Should().Be(50);
        user.FindProperty(nameof(User.Language))!.GetMaxLength().Should().Be(10);
        user.FindProperty(nameof(User.ThemePreference))!.GetMaxLength().Should().Be(10);
        user.FindProperty(nameof(User.TimeZone))!.GetMaxLength().Should().Be(100);

        context.Model.FindEntityType(typeof(Goal))!.FindProperty(nameof(Goal.Unit))!.GetMaxLength().Should().Be(50);
    }

    [Fact]
    public void Model_EncryptedTextColumns_CarryAdvisoryMaxLengthWithoutNarrowingStorageType()
    {
        var encryptionService = Substitute.For<IEncryptionService>();

        using var context = CreatePostgresContext(encryptionService);

        var goalTitle = context.Model.FindEntityType(typeof(Goal))!.FindProperty(nameof(Goal.Title))!;
        goalTitle.GetMaxLength().Should().Be(200);
        goalTitle.GetColumnType().Should().Be("text");
        goalTitle.GetValueConverter().Should().NotBeNull();

        var note = context.Model.FindEntityType(typeof(HabitLog))!.FindProperty(nameof(HabitLog.Note))!;
        note.GetMaxLength().Should().Be(1000);
        note.GetColumnType().Should().Be("text");
        note.GetValueConverter().Should().NotBeNull();
    }

    [Fact]
    public void Model_HabitsAndTags_CascadeDeleteWithTheirOwningUser()
    {
        using var context = CreateContext();

        AssertCascadingUserForeignKey(context.Model.FindEntityType(typeof(Habit))!, nameof(Habit.UserId));
        AssertCascadingUserForeignKey(context.Model.FindEntityType(typeof(Tag))!, nameof(Tag.UserId));
    }

    private static void AssertCascadingUserForeignKey(
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entityType,
        string foreignKeyPropertyName)
    {
        var userForeignKey = entityType.GetForeignKeys().Single(fk =>
            fk.PrincipalEntityType.ClrType == typeof(User)
            && fk.Properties.Count == 1
            && fk.Properties[0].Name == foreignKeyPropertyName);

        userForeignKey.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
    }

    [Theory]
    [MemberData(nameof(ForeignKeyDeleteBehaviorCases))]
    public void Model_ForeignKey_HasExpectedOnDeleteBehavior(
        Type dependentType, string foreignKeyProperty, Type principalType, DeleteBehavior expectedBehavior)
    {
        using var context = CreatePostgresContext();
        var dependent = context.Model.FindEntityType(dependentType)!;

        var matching = dependent.GetForeignKeys()
            .Where(foreignKey => foreignKey.Properties.Count == 1
                && foreignKey.Properties[0].Name == foreignKeyProperty
                && foreignKey.PrincipalEntityType.ClrType == principalType)
            .ToList();

        matching.Should().HaveCount(1,
            "{0}.{1} -> {2} must be configured as exactly one foreign key",
            dependentType.Name, foreignKeyProperty, principalType.Name);
        matching[0].DeleteBehavior.Should().Be(expectedBehavior,
            "{0}.{1} -> {2} must keep its intended delete behavior",
            dependentType.Name, foreignKeyProperty, principalType.Name);
    }

    [Theory]
    [MemberData(nameof(UniqueIndexCases))]
    public void Model_UniqueIndex_IsConfiguredWithExpectedKeySet(Type entityType, string[] keyProperties)
    {
        using var context = CreatePostgresContext();
        var entity = context.Model.FindEntityType(entityType)!;

        var matching = entity.GetIndexes()
            .Where(index => index.IsUnique && KeyOf(index).SequenceEqual(keyProperties))
            .ToList();

        matching.Should().HaveCount(1,
            "unique index {0}({1}) must exist exactly once",
            entityType.Name, string.Join(", ", keyProperties));
    }

    [Theory]
    [MemberData(nameof(FilteredIndexCases))]
    public void Model_ConditionalIndex_CarriesExpectedFilterPredicate(
        Type entityType, string[] keyProperties, string expectedFilter)
    {
        using var context = CreatePostgresContext();
        var entity = context.Model.FindEntityType(entityType)!;

        var filters = entity.GetIndexes()
            .Where(index => KeyOf(index).SequenceEqual(keyProperties))
            .Select(index => index.GetFilter())
            .ToList();

        filters.Should().ContainSingle(filter => filter == expectedFilter,
            "index {0}({1}) must carry the partial-index predicate {2}",
            entityType.Name, string.Join(", ", keyProperties), expectedFilter);
    }

    [Theory]
    [MemberData(nameof(SoftDeleteQueryFilterCases))]
    public void Model_SoftDeletableEntity_HasQueryFilterOnDeletionFlag(Type entityType, string deletionFlagProperty)
    {
        using var context = CreatePostgresContext();
        var entity = context.Model.FindEntityType(entityType)!;

        var queryFilter = entity.GetDeclaredQueryFilters().Should().ContainSingle(
            "{0} must hide soft-deleted rows via a single global query filter", entityType.Name).Which;
        queryFilter.Expression!.ToString().Should().Contain(deletionFlagProperty,
            "{0} query filter must gate on {1}", entityType.Name, deletionFlagProperty);
    }

    [Theory]
    [MemberData(nameof(PerformanceIndexCases))]
    public void Model_PerformanceCriticalIndex_IsConfigured(Type entityType, string[] keyProperties)
    {
        using var context = CreatePostgresContext();
        var entity = context.Model.FindEntityType(entityType)!;

        entity.GetIndexes()
            .Any(index => KeyOf(index).SequenceEqual(keyProperties))
            .Should().BeTrue(
                "performance-critical index {0}({1}) must exist",
                entityType.Name, string.Join(", ", keyProperties));
    }

    public static TheoryData<Type, string, Type, DeleteBehavior> ForeignKeyDeleteBehaviorCases()
    {
        (Type Dependent, string ForeignKey, Type Principal, DeleteBehavior Behavior)[] cases =
        {
            (typeof(Habit), nameof(Habit.UserId), typeof(User), DeleteBehavior.Cascade),
            (typeof(Habit), nameof(Habit.ParentHabitId), typeof(Habit), DeleteBehavior.Cascade),
            (typeof(HabitLog), nameof(HabitLog.HabitId), typeof(Habit), DeleteBehavior.Cascade),
            (typeof(Tag), nameof(Tag.UserId), typeof(User), DeleteBehavior.Cascade),
            (typeof(PushSubscription), nameof(PushSubscription.UserId), typeof(User), DeleteBehavior.Cascade),
            (typeof(SentProactiveCheckin), nameof(SentProactiveCheckin.UserId), typeof(User), DeleteBehavior.Cascade),
            (typeof(SentStreakFreezeAlert), nameof(SentStreakFreezeAlert.UserId), typeof(User), DeleteBehavior.Cascade),
            (typeof(ProcessedRequest), nameof(ProcessedRequest.UserId), typeof(User), DeleteBehavior.Cascade),
            (typeof(AiFactExtractionBatch), nameof(AiFactExtractionBatch.UserId), typeof(User), DeleteBehavior.Cascade),
            (typeof(PendingClarification), nameof(PendingClarification.UserId), typeof(User), DeleteBehavior.Cascade),
            (typeof(ChecklistTemplate), nameof(ChecklistTemplate.UserId), typeof(User), DeleteBehavior.Cascade),
            (typeof(StreakFreeze), nameof(StreakFreeze.UserId), typeof(User), DeleteBehavior.Cascade),
            (typeof(XpAwardLog), nameof(XpAwardLog.UserId), typeof(User), DeleteBehavior.Cascade),
            (typeof(UserSession), nameof(UserSession.UserId), typeof(User), DeleteBehavior.Cascade),
            (typeof(ApiKey), nameof(ApiKey.UserId), typeof(User), DeleteBehavior.Cascade),
            (typeof(GoogleCalendarSyncSuggestion), nameof(GoogleCalendarSyncSuggestion.UserId), typeof(User), DeleteBehavior.Cascade),
            (typeof(GoalProgressLog), nameof(GoalProgressLog.GoalId), typeof(Goal), DeleteBehavior.Cascade),
            (typeof(AccountabilityPairHabit), nameof(AccountabilityPairHabit.PairId), typeof(AccountabilityPair), DeleteBehavior.Cascade),
            (typeof(AccountabilityPairHabit), nameof(AccountabilityPairHabit.HabitId), typeof(Habit), DeleteBehavior.Cascade),
            (typeof(AccountabilityCheckIn), nameof(AccountabilityCheckIn.PairId), typeof(AccountabilityPair), DeleteBehavior.Cascade),
            (typeof(ChallengeParticipant), nameof(ChallengeParticipant.ChallengeId), typeof(Challenge), DeleteBehavior.Cascade),
            (typeof(ChallengeParticipantHabit), nameof(ChallengeParticipantHabit.ChallengeParticipantId), typeof(ChallengeParticipant), DeleteBehavior.Cascade),
            (typeof(ChallengeParticipantHabit), nameof(ChallengeParticipantHabit.HabitId), typeof(Habit), DeleteBehavior.Cascade),
            (typeof(Friendship), nameof(Friendship.RequesterId), typeof(User), DeleteBehavior.Restrict),
            (typeof(Friendship), nameof(Friendship.AddresseeId), typeof(User), DeleteBehavior.Restrict),
            (typeof(Cheer), nameof(Cheer.SenderId), typeof(User), DeleteBehavior.Restrict),
            (typeof(Cheer), nameof(Cheer.RecipientId), typeof(User), DeleteBehavior.Restrict),
            (typeof(Cheer), nameof(Cheer.HabitId), typeof(Habit), DeleteBehavior.SetNull),
            (typeof(BlockedUser), nameof(BlockedUser.BlockerId), typeof(User), DeleteBehavior.Restrict),
            (typeof(BlockedUser), nameof(BlockedUser.BlockedId), typeof(User), DeleteBehavior.Restrict),
            (typeof(Report), nameof(Report.ReporterId), typeof(User), DeleteBehavior.Restrict),
            (typeof(Report), nameof(Report.ReportedUserId), typeof(User), DeleteBehavior.Restrict),
            (typeof(Report), nameof(Report.CheerId), typeof(Cheer), DeleteBehavior.SetNull),
            (typeof(AccountabilityPair), nameof(AccountabilityPair.RequesterId), typeof(User), DeleteBehavior.Restrict),
            (typeof(AccountabilityPair), nameof(AccountabilityPair.AddresseeId), typeof(User), DeleteBehavior.Restrict),
            (typeof(FriendFeedEvent), nameof(FriendFeedEvent.ActorUserId), typeof(User), DeleteBehavior.Restrict),
            (typeof(Challenge), nameof(Challenge.CreatorId), typeof(User), DeleteBehavior.Restrict),
            (typeof(ChallengeParticipant), nameof(ChallengeParticipant.UserId), typeof(User), DeleteBehavior.Restrict),
        };
        var data = new TheoryData<Type, string, Type, DeleteBehavior>();
        foreach (var entry in cases)
            data.Add(entry.Dependent, entry.ForeignKey, entry.Principal, entry.Behavior);
        return data;
    }

    public static TheoryData<Type, string[]> UniqueIndexCases()
    {
        (Type Entity, string[] Keys)[] cases =
        {
            (typeof(User), new[] { nameof(User.Email) }),
            (typeof(User), new[] { nameof(User.ReferralCode) }),
            (typeof(User), new[] { nameof(User.PlayPurchaseToken) }),
            (typeof(User), new[] { nameof(User.PublicProfileSlug) }),
            (typeof(Habit), new[] { nameof(Habit.UserId), nameof(Habit.GoogleEventId) }),
            (typeof(HabitLog), new[] { nameof(HabitLog.HabitId), nameof(HabitLog.Date) }),
            (typeof(Tag), new[] { nameof(Tag.UserId), nameof(Tag.Name) }),
            (typeof(PushSubscription), new[] { nameof(PushSubscription.Endpoint) }),
            (typeof(SentReminder), new[]
            {
                nameof(SentReminder.HabitId), nameof(SentReminder.Date), nameof(SentReminder.MinutesBefore),
                nameof(SentReminder.ReminderTimeUtc), nameof(SentReminder.When),
            }),
            (typeof(SentSlipAlert), new[] { nameof(SentSlipAlert.HabitId), nameof(SentSlipAlert.WeekStart) }),
            (typeof(SentProactiveCheckin), new[] { nameof(SentProactiveCheckin.UserId), nameof(SentProactiveCheckin.Date) }),
            (typeof(SentStreakFreezeAlert), new[] { nameof(SentStreakFreezeAlert.UserId), nameof(SentStreakFreezeAlert.FrozenDate) }),
            (typeof(ProcessedPlayNotification), new[] { nameof(ProcessedPlayNotification.MessageId) }),
            (typeof(ProcessedStripeEvent), new[] { nameof(ProcessedStripeEvent.EventId) }),
            (typeof(ProcessedRequest), new[]
            {
                nameof(ProcessedRequest.UserId), nameof(ProcessedRequest.IdempotencyKey), nameof(ProcessedRequest.RequestType),
            }),
            (typeof(AiFactExtractionBatch), new[] { nameof(AiFactExtractionBatch.BatchId) }),
            (typeof(AiUsageDaily), new[] { nameof(AiUsageDaily.Date), nameof(AiUsageDaily.Model), nameof(AiUsageDaily.Purpose) }),
            (typeof(Referral), new[] { nameof(Referral.ReferredUserId) }),
            (typeof(UserAchievement), new[] { nameof(UserAchievement.UserId), nameof(UserAchievement.AchievementId) }),
            (typeof(StreakFreeze), new[] { nameof(StreakFreeze.UserId), nameof(StreakFreeze.UsedOnDate) }),
            (typeof(UserSession), new[] { nameof(UserSession.TokenHash) }),
            (typeof(DistributedRateLimitBucket), new[]
            {
                nameof(DistributedRateLimitBucket.PolicyName), nameof(DistributedRateLimitBucket.PartitionKey),
                nameof(DistributedRateLimitBucket.WindowStartUtc),
            }),
            (typeof(ContentBlock), new[] { nameof(ContentBlock.Key), nameof(ContentBlock.Locale) }),
            (typeof(GoogleCalendarSyncSuggestion), new[]
            {
                nameof(GoogleCalendarSyncSuggestion.UserId), nameof(GoogleCalendarSyncSuggestion.GoogleEventId),
            }),
            (typeof(BlockedUser), new[] { nameof(BlockedUser.BlockerId), nameof(BlockedUser.BlockedId) }),
            (typeof(FriendFeedEvent), new[] { nameof(FriendFeedEvent.ActorUserId), nameof(FriendFeedEvent.AchievementId) }),
            (typeof(FriendFeedEvent), new[] { nameof(FriendFeedEvent.ActorUserId), nameof(FriendFeedEvent.Type), nameof(FriendFeedEvent.Value) }),
            (typeof(Challenge), new[] { nameof(Challenge.JoinCode) }),
            (typeof(ChallengeParticipant), new[] { nameof(ChallengeParticipant.ChallengeId), nameof(ChallengeParticipant.UserId) }),
            (typeof(ChallengeParticipantHabit), new[]
            {
                nameof(ChallengeParticipantHabit.ChallengeParticipantId), nameof(ChallengeParticipantHabit.HabitId),
            }),
            (typeof(AccountabilityPairHabit), new[]
            {
                nameof(AccountabilityPairHabit.PairId), nameof(AccountabilityPairHabit.UserId), nameof(AccountabilityPairHabit.HabitId),
            }),
            (typeof(AccountabilityCheckIn), new[]
            {
                nameof(AccountabilityCheckIn.PairId), nameof(AccountabilityCheckIn.UserId), nameof(AccountabilityCheckIn.Date),
            }),
        };
        var data = new TheoryData<Type, string[]>();
        foreach (var entry in cases)
            data.Add(entry.Entity, entry.Keys);
        return data;
    }

    public static TheoryData<Type, string[], string> FilteredIndexCases()
    {
        (Type Entity, string[] Keys, string Filter)[] cases =
        {
            (typeof(Tag), new[] { nameof(Tag.UserId), nameof(Tag.Name) }, "\"IsDeleted\" = FALSE"),
            (typeof(Notification), new[] { nameof(Notification.Url) }, "\"Url\" IS NOT NULL"),
            (typeof(User), new[] { nameof(User.ReferralCode) }, "\"ReferralCode\" IS NOT NULL"),
            (typeof(User), new[] { nameof(User.PlayPurchaseToken) }, "\"PlayPurchaseToken\" IS NOT NULL"),
            (typeof(User), new[] { nameof(User.PublicProfileSlug) }, "\"PublicProfileSlug\" IS NOT NULL"),
            (typeof(User), new[] { nameof(User.GoogleCalendarAutoSyncEnabled), nameof(User.GoogleCalendarLastSyncedAt) }, "\"GoogleCalendarAutoSyncEnabled\" = TRUE"),
            (typeof(Habit), new[] { nameof(Habit.UserId), nameof(Habit.GoogleEventId) }, "\"GoogleEventId\" IS NOT NULL AND \"IsDeleted\" = FALSE"),
            (typeof(HabitLog), new[] { nameof(HabitLog.HabitId), nameof(HabitLog.Date) }, "\"Value\" > 0 AND NOT \"IsDeleted\""),
            (typeof(FriendFeedEvent), new[] { nameof(FriendFeedEvent.ActorUserId), nameof(FriendFeedEvent.AchievementId) }, "\"AchievementId\" IS NOT NULL"),
            (typeof(FriendFeedEvent), new[] { nameof(FriendFeedEvent.ActorUserId), nameof(FriendFeedEvent.Type), nameof(FriendFeedEvent.Value) }, "\"AchievementId\" IS NULL"),
            (typeof(ChallengeParticipant), new[] { nameof(ChallengeParticipant.ChallengeId), nameof(ChallengeParticipant.UserId) }, "\"LeftAtUtc\" IS NULL"),
        };
        var data = new TheoryData<Type, string[], string>();
        foreach (var entry in cases)
            data.Add(entry.Entity, entry.Keys, entry.Filter);
        return data;
    }

    public static TheoryData<Type, string> SoftDeleteQueryFilterCases()
    {
        (Type Entity, string Flag)[] cases =
        {
            (typeof(User), nameof(User.IsDeactivated)),
            (typeof(Habit), nameof(Habit.IsDeleted)),
            (typeof(HabitLog), nameof(HabitLog.IsDeleted)),
            (typeof(Tag), nameof(Tag.IsDeleted)),
            (typeof(Notification), nameof(Notification.IsDeleted)),
            (typeof(UserFact), nameof(UserFact.IsDeleted)),
            (typeof(Goal), nameof(Goal.IsDeleted)),
            (typeof(GoalProgressLog), nameof(GoalProgressLog.IsDeleted)),
            (typeof(ChecklistTemplate), nameof(ChecklistTemplate.IsDeleted)),
            (typeof(Challenge), nameof(Challenge.IsDeleted)),
        };
        var data = new TheoryData<Type, string>();
        foreach (var entry in cases)
            data.Add(entry.Entity, entry.Flag);
        return data;
    }

    public static TheoryData<Type, string[]> PerformanceIndexCases()
    {
        (Type Entity, string[] Keys)[] cases =
        {
            (typeof(Habit), new[] { nameof(Habit.UserId) }),
            (typeof(Habit), new[] { nameof(Habit.UserId), nameof(Habit.UpdatedAtUtc) }),
            (typeof(HabitLog), new[] { nameof(HabitLog.HabitId), nameof(HabitLog.Date) }),
            (typeof(HabitLog), new[] { nameof(HabitLog.HabitId), nameof(HabitLog.UpdatedAtUtc) }),
            (typeof(Tag), new[] { nameof(Tag.UserId), nameof(Tag.UpdatedAtUtc) }),
            (typeof(Notification), new[] { nameof(Notification.UserId), nameof(Notification.IsRead) }),
            (typeof(Notification), new[] { nameof(Notification.UserId), nameof(Notification.UpdatedAtUtc) }),
            (typeof(Goal), new[] { nameof(Goal.UserId) }),
            (typeof(Goal), new[] { nameof(Goal.UserId), nameof(Goal.UpdatedAtUtc) }),
            (typeof(GoalProgressLog), new[] { nameof(GoalProgressLog.GoalId) }),
            (typeof(GoalProgressLog), new[] { nameof(GoalProgressLog.GoalId), nameof(GoalProgressLog.UpdatedAtUtc) }),
            (typeof(ChecklistTemplate), new[] { nameof(ChecklistTemplate.UserId) }),
            (typeof(ChecklistTemplate), new[] { nameof(ChecklistTemplate.UserId), nameof(ChecklistTemplate.UpdatedAtUtc) }),
            (typeof(PushSubscription), new[] { nameof(PushSubscription.UserId) }),
            (typeof(UserSession), new[] { nameof(UserSession.UserId) }),
            (typeof(ApiKey), new[] { nameof(ApiKey.UserId) }),
            (typeof(GoogleCalendarSyncSuggestion), new[] { nameof(GoogleCalendarSyncSuggestion.UserId) }),
            (typeof(ProcessedRequest), new[] { nameof(ProcessedRequest.CreatedAtUtc) }),
            (typeof(Cheer), new[] { nameof(Cheer.SenderId), nameof(Cheer.CreatedAtUtc) }),
        };
        var data = new TheoryData<Type, string[]>();
        foreach (var entry in cases)
            data.Add(entry.Entity, entry.Keys);
        return data;
    }

    private static IEnumerable<string> KeyOf(IReadOnlyIndex index)
        => index.Properties.Select(property => property.Name);

    private static OrbitDbContext CreateContext(IEncryptionService? encryptionService = null)
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ReplaceService<IModelCacheKeyFactory, EncryptionAwareModelCacheKeyFactory>()
            .Options;

        return new OrbitDbContext(options, encryptionService);
    }

    private sealed class EncryptionAwareModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
        {
            var hasEncryption = context is OrbitDbContext orbit && orbit.HasEncryptionService;
            return (context.GetType(), hasEncryption, designTime);
        }
    }

    private static OrbitDbContext CreatePostgresContext(IEncryptionService? encryptionService = null)
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=localhost;Database=orbit-tests;Username=test;Password=test")
            .ReplaceService<IModelCacheKeyFactory, EncryptionAwareModelCacheKeyFactory>()
            .Options;

        return new OrbitDbContext(options, encryptionService);
    }
}
