using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Infrastructure.Persistence;

public class OrbitDbContext : DbContext
{
    private const string JsonbColumnType = "jsonb";
    private const string EmptyJsonArraySql = "'[]'::jsonb";
    private readonly IEncryptionService? _encryptionService;

    public OrbitDbContext(DbContextOptions<OrbitDbContext> options, IEncryptionService? encryptionService = null)
        : base(options)
    {
        _encryptionService = encryptionService;
    }

    /// <summary>
    /// Indicates whether this context was constructed with an encryption service.
    /// Used by tests to distinguish cached model variants.
    /// </summary>
    public bool HasEncryptionService => _encryptionService is not null;

    public DbSet<OwedSubscriptionCancellation> OwedSubscriptionCancellations => Set<OwedSubscriptionCancellation>();
    public DbSet<User> Users => Set<User>();
    public DbSet<MarketingContact> MarketingContacts => Set<MarketingContact>();
    public DbSet<Habit> Habits => Set<Habit>();
    public DbSet<HabitSkipUndo> HabitSkipUndos => Set<HabitSkipUndo>();
    public DbSet<HabitLog> HabitLogs => Set<HabitLog>();
    public DbSet<AppConfig> AppConfigs => Set<AppConfig>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<SentReminder> SentReminders => Set<SentReminder>();
    public DbSet<SentSlipAlert> SentSlipAlerts => Set<SentSlipAlert>();
    public DbSet<ProactiveItem> ProactiveItems => Set<ProactiveItem>();
    public DbSet<ProactiveCalendarDay> ProactiveCalendarDays => Set<ProactiveCalendarDay>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Goal> Goals => Set<Goal>();
    public DbSet<GoalProgressLog> GoalProgressLogs => Set<GoalProgressLog>();
    public DbSet<Referral> Referrals => Set<Referral>();
    public DbSet<UserAchievement> UserAchievements => Set<UserAchievement>();
    public DbSet<StreakFreeze> StreakFreezes => Set<StreakFreeze>();
    public DbSet<XpAwardLog> XpAwardLogs => Set<XpAwardLog>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<UserSessionRefreshToken> UserSessionRefreshTokens => Set<UserSessionRefreshToken>();
    public DbSet<AiBudgetCall> AiBudgetCalls => Set<AiBudgetCall>();
    public DbSet<OAuthState> OAuthStates => Set<OAuthState>();
    public DbSet<DesktopHandoff> DesktopHandoffs => Set<DesktopHandoff>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<PendingAgentOperationState> PendingAgentOperations => Set<PendingAgentOperationState>();
    public DbSet<IssuedAstraBlock> IssuedAstraBlocks => Set<IssuedAstraBlock>();
    public DbSet<PendingClarification> PendingClarifications => Set<PendingClarification>();
    public DbSet<AgentStepUpChallengeState> AgentStepUpChallenges => Set<AgentStepUpChallengeState>();
    public DbSet<AgentAuditLog> AgentAuditLogs => Set<AgentAuditLog>();
    public DbSet<DistributedRateLimitBucket> DistributedRateLimitBuckets => Set<DistributedRateLimitBucket>();
    public DbSet<ChecklistTemplate> ChecklistTemplates => Set<ChecklistTemplate>();
    public DbSet<AppFeatureFlagAccount> AppFeatureFlagAccounts => Set<AppFeatureFlagAccount>();
    public DbSet<AppFeatureFlag> AppFeatureFlags => Set<AppFeatureFlag>();
    public DbSet<ContentBlock> ContentBlocks => Set<ContentBlock>();
    public DbSet<GoogleCalendarSyncSuggestion> GoogleCalendarSyncSuggestions => Set<GoogleCalendarSyncSuggestion>();
    public DbSet<NotionConnection> NotionConnections => Set<NotionConnection>();
    public DbSet<NotionTask> NotionTasks => Set<NotionTask>();
    public DbSet<NotionOAuthState> NotionOAuthStates => Set<NotionOAuthState>();
    public DbSet<ProcessedAppStoreNotification> ProcessedAppStoreNotifications => Set<ProcessedAppStoreNotification>();
    public DbSet<ProcessedPlayNotification> ProcessedPlayNotifications => Set<ProcessedPlayNotification>();
    public DbSet<ProcessedStripeEvent> ProcessedStripeEvents => Set<ProcessedStripeEvent>();
    public DbSet<ChatTurn> ChatTurns => Set<ChatTurn>();
    public DbSet<ProcessedRequest> ProcessedRequests => Set<ProcessedRequest>();
    public DbSet<ConversationTurn> ConversationTurns => Set<ConversationTurn>();
    public DbSet<AiUsageDaily> AiUsageDaily => Set<AiUsageDaily>();
    public DbSet<ClosedMonthRecap> ClosedMonthRecaps => Set<ClosedMonthRecap>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TransactionCommit>(entity =>
        {
            entity.ToTable("TransactionCommits");
            entity.HasKey(commit => commit.Id);
            entity.HasIndex(commit => commit.CreatedAtUtc);
        });
        var isPostgres = Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

        EncryptionValueConverter? encConverter = null;
        NullableEncryptionValueConverter? nullableEncConverter = null;

        if (_encryptionService is not null)
        {
            encConverter = new EncryptionValueConverter(_encryptionService);
            nullableEncConverter = new NullableEncryptionValueConverter(_encryptionService);
        }
        modelBuilder.Entity<OwedSubscriptionCancellation>(entity =>
        {
            entity.HasKey(record => record.Id);
            entity.Property(record => record.ProviderIdentifier).IsRequired();
            entity.HasIndex(record => record.NextAttemptAtUtc);
            entity.HasIndex(record => new { record.Provider, record.ProviderIdentifier }).IsUnique();
        });
        ConfigureUserEntity(modelBuilder, nullableEncConverter);
        ConfigureMarketingContactEntity(modelBuilder);
        ConfigureHabitEntity(modelBuilder, isPostgres, encConverter, nullableEncConverter);

        if (isPostgres)
            ConfigureConcurrencyTokens(modelBuilder);
        ConfigureHabitLogEntity(modelBuilder, nullableEncConverter);
        modelBuilder.Entity<HabitSkipUndo>(entity =>
        {
            entity.HasIndex(skip => new { skip.UserId, skip.HabitId });
            entity.Property(skip => skip.ExpectedLogState).HasMaxLength(64);
            entity.Property(skip => skip.IsUndone).IsConcurrencyToken();
            entity.HasOne<Habit>().WithMany().HasForeignKey(skip => skip.HabitId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        ConfigureGoogleCalendarSyncSuggestionEntity(modelBuilder, encConverter, nullableEncConverter);
        ConfigureNotionEntities(modelBuilder, encConverter, nullableEncConverter);
        ConfigureTagEntity(modelBuilder);
        ConfigurePushSubscriptionEntity(modelBuilder);
        ConfigureSentReminderEntity(modelBuilder);
        ConfigureSentSlipAlertEntity(modelBuilder);
        ConfigureProactiveEntities(modelBuilder);
        modelBuilder.Entity<ProcessedAppStoreNotification>(entity =>
        {
            entity.HasKey(notification => notification.Id);
            entity.HasIndex(notification => notification.NotificationUuid).IsUnique();
            entity.Property(notification => notification.Environment).HasMaxLength(20).IsRequired();
        });
        ConfigureProcessedPlayNotificationEntity(modelBuilder);
        ConfigureProcessedStripeEventEntity(modelBuilder);
        ConfigureProcessedRequestEntity(modelBuilder);
        modelBuilder.Entity<ChatTurn>(entity =>
        {
            entity.HasIndex(turn => new { turn.UserId, turn.KeyHash }).IsUnique();
            entity.Property(turn => turn.KeyHash).HasMaxLength(64).IsRequired();
            entity.Property(turn => turn.Fingerprint).HasMaxLength(64).IsRequired();
            entity.HasOne<User>().WithMany().HasForeignKey(turn => turn.UserId).OnDelete(DeleteBehavior.Cascade);
            if (nullableEncConverter is not null)
                entity.Property(turn => turn.TerminalEvent).HasConversion(nullableEncConverter);
        });
        ConfigureAiUsageDailyEntity(modelBuilder);
        modelBuilder.Entity<ConversationTurn>(entity =>
        {
            entity.HasKey(turn => turn.Id);
            entity.Property(turn => turn.Role).HasMaxLength(20).IsRequired();
            entity.Property(turn => turn.Model).HasMaxLength(200);
            entity.Property(turn => turn.ToolCallsJson).IsRequired();
            entity.HasIndex(turn => new { turn.UserId, turn.CreatedAtUtc });
            entity.HasIndex(turn => turn.CreatedAtUtc);
            entity.HasIndex(turn => new { turn.TurnId, turn.Role }).IsUnique();
            entity.HasOne<User>().WithMany().HasForeignKey(turn => turn.UserId).OnDelete(DeleteBehavior.Cascade);
            if (nullableEncConverter is not null)
            {
                entity.Property(turn => turn.Content).HasConversion(nullableEncConverter);
                entity.Property(turn => turn.ResponseJson).HasConversion(nullableEncConverter);
            }
            if (encConverter is not null)
                entity.Property(turn => turn.ToolCallsJson).HasConversion(encConverter);
        });
        ConfigureNotificationEntity(modelBuilder);
        ConfigureGoalEntity(modelBuilder, encConverter, nullableEncConverter);
        ConfigureGoalProgressLogEntity(modelBuilder, nullableEncConverter);
        ConfigureReferralEntity(modelBuilder);
        ConfigureUserAchievementEntity(modelBuilder);
        ConfigureStreakFreezeEntity(modelBuilder);
        ConfigureXpAwardLogEntity(modelBuilder);
        ConfigureUserSessionEntity(modelBuilder);
        ConfigureUserSessionRefreshTokenEntity(modelBuilder);
        ConfigureApiKeyEntity(modelBuilder);
        modelBuilder.Entity<AiBudgetCall>(entity =>
        {
            entity.Property<long>("ChargedTokens");
            entity.Property(call => call.ChargedMicroDollars).HasDefaultValue(0L);
            entity.HasIndex(call => new { call.UserId, call.LocalDate });
            entity.HasOne<User>().WithMany().HasForeignKey(call => call.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<OAuthState>(entity =>
        {
            entity.HasIndex(state => state.SecretHash).IsUnique();
            entity.HasIndex(state => new { state.Kind, state.ExpiresAtUtc });
            entity.Property(state => state.SecretHash).HasMaxLength(64);
            entity.Property(state => state.Kind).HasMaxLength(30);
            entity.Property(state => state.Payload).HasConversion(encConverter).HasColumnType("text");
        });
        modelBuilder.Entity<DesktopHandoff>(entity =>
        {
            entity.Property(handoff => handoff.CodeHash).HasMaxLength(64).IsRequired();
            entity.Property(handoff => handoff.State).HasMaxLength(DesktopHandoff.MaxStateLength).IsRequired();
            entity.Property(handoff => handoff.CodeChallenge).HasMaxLength(43).IsRequired();
            entity.Property(handoff => handoff.RedirectUri).HasMaxLength(2048).IsRequired();
            entity.HasIndex(handoff => handoff.CodeHash).IsUnique();
            entity.HasIndex(handoff => handoff.ExpiresAtUtc);
            entity.HasOne<User>().WithMany().HasForeignKey(handoff => handoff.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        ConfigurePendingAgentOperationEntity(modelBuilder);
        ConfigureAgentStepUpChallengeEntity(modelBuilder);
        ConfigurePendingClarificationEntity(modelBuilder);
        modelBuilder.Entity<IssuedAstraBlock>(entity =>
        {
            entity.HasIndex(block => new { block.UserId, block.CreatedAtUtc });
            entity.HasIndex(block => block.ExpiresAtUtc);
            entity.Property(block => block.Form).HasMaxLength(16);
            entity.Property(block => block.DefinitionJson).HasColumnType(JsonbColumnType);
            entity.Property(block => block.AnswerJson).HasColumnType(JsonbColumnType);
            entity.Property(block => block.ResponseJson).HasColumnType("text");
            entity.Property(block => block.CommittedResponseJson).HasColumnType("text");
            entity.HasOne<User>().WithMany().HasForeignKey(block => block.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        ConfigureAgentAuditLogEntity(modelBuilder);
        ConfigureDistributedRateLimitBucketEntity(modelBuilder);
        ConfigureAppConfigEntity(modelBuilder);
        ConfigureChecklistTemplateEntity(modelBuilder);
        ConfigureAppFeatureFlagEntity(modelBuilder);
        modelBuilder.Entity<AppFeatureFlagAccount>(entity =>
        {
            entity.HasKey(account => new { account.FlagKey, account.UserId });
            entity.Property(account => account.FlagKey).HasMaxLength(100);
            entity.HasOne<AppFeatureFlag>().WithMany().HasForeignKey(account => account.FlagKey).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<User>().WithMany().HasForeignKey(account => account.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        ConfigureContentBlockEntity(modelBuilder);
        ConfigureClosedMonthRecapEntity(modelBuilder);
    }

    private static void ConfigureMarketingContactEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MarketingContact>(entity =>
        {
            entity.HasIndex(contact => contact.Email).IsUnique();
            entity.Property(contact => contact.Email).HasMaxLength(254).IsRequired();
            entity.Property(contact => contact.Language).HasMaxLength(10).IsRequired();
            entity.Property(contact => contact.Source).HasMaxLength(32).IsRequired();
        });
    }

    private static void ConfigureClosedMonthRecapEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ClosedMonthRecap>(entity =>
        {
            entity.HasIndex(recap => new { recap.UserId, recap.DateFrom, recap.DateTo }).IsUnique();
            entity.Property(recap => recap.ResponseJson).HasColumnType(JsonbColumnType).IsRequired();
            entity.HasOne<User>().WithMany().HasForeignKey(recap => recap.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureConcurrencyTokens(ModelBuilder modelBuilder)
    {
        MapXminToken(modelBuilder.Entity<User>());
        MapXminToken(modelBuilder.Entity<Goal>());
        MapXminToken(modelBuilder.Entity<Referral>());
        MapXminToken(modelBuilder.Entity<UserSession>());
    }

    private static void MapXminToken<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> entity)
        where T : class
    {
        entity.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();
    }

    private static void ConfigureTagEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tag>(entity =>
        {
            entity.HasIndex(t => new { t.UserId, t.Name }).IsUnique().HasFilter("\"IsDeleted\" = FALSE");
            entity.HasIndex(t => new { t.UserId, t.IsDeleted });
            entity.HasIndex(t => new { t.UserId, t.UpdatedAtUtc });
            entity.HasQueryFilter(t => !t.IsDeleted);

            entity.Property(t => t.Name).IsRequired().HasMaxLength(50);
            entity.Property(t => t.Color).IsRequired().HasMaxLength(50);

            entity.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(t => t.Habits)
                .WithMany(h => h.Tags)
                .UsingEntity("HabitTags",
                    l => l.HasOne(typeof(Habit)).WithMany().HasForeignKey(nameof(Habit) + nameof(Habit.Id)).OnDelete(DeleteBehavior.Cascade),
                    r => r.HasOne(typeof(Tag)).WithMany().HasForeignKey(nameof(Tag) + nameof(Tag.Id)).OnDelete(DeleteBehavior.Cascade));
        });
    }

    private static void ConfigurePushSubscriptionEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PushSubscription>(entity =>
        {
            entity.HasIndex(s => s.UserId);
            entity.HasIndex(s => s.Endpoint).IsUnique();
            entity.Property(s => s.ApnsTopic).HasMaxLength(255);
            entity.HasIndex(s => new { s.UserId, s.DeviceId, s.P256dh }).IsUnique()
                .HasFilter("\"DeviceId\" IS NOT NULL");
            entity.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureSentReminderEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SentReminder>(entity =>
        {
            entity.HasIndex(r => new { r.HabitId, r.Date, r.MinutesBefore, r.ReminderTimeUtc, r.When })
                .IsUnique()
                .AreNullsDistinct(false);
        });
    }

    private static void ConfigureSentSlipAlertEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SentSlipAlert>(entity =>
        {
            entity.HasIndex(a => new { a.HabitId, a.WeekStart }).IsUnique();
        });
    }

    private static void ConfigureProactiveEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>().Ignore(u => u.EffectiveProactivePushEnabled);
        modelBuilder.Entity<ProactiveItem>(entity =>
        {
            entity.HasIndex(a => new { a.UserId, a.Trigger, a.LocalDate }).IsUnique();
            entity.HasIndex(a => new { a.UserId, a.LocalDate }).IsUnique()
                .HasFilter("\"PushedAtUtc\" IS NOT NULL");
            entity.HasIndex(a => new { a.UserId, a.Trigger, a.EndedAtUtc });
            entity.Property(a => a.Text).HasMaxLength(140).IsRequired();
            entity.Property(a => a.ActionLabel).IsRequired();
            entity.Property(a => a.ActionPrompt).IsRequired();
            entity.Property(a => a.FactsJson).IsRequired();
            entity.Ignore(a => a.Ignored);
            entity.Property(a => a.ShownAtUtc).IsConcurrencyToken();
            entity.Property(a => a.OpenedAtUtc).IsConcurrencyToken();
            entity.Property(a => a.ActedAtUtc).IsConcurrencyToken();
            entity.Property(a => a.EndedAtUtc).IsConcurrencyToken();
            entity.HasOne<User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ProactiveCalendarDay>(entity =>
        {
            entity.HasIndex(a => new { a.UserId, a.LocalDate }).IsUnique();
            entity.HasOne<User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureProcessedPlayNotificationEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcessedPlayNotification>(entity =>
        {
            entity.HasIndex(p => p.MessageId).IsUnique();
            entity.Property(p => p.MessageId).IsRequired().HasMaxLength(255);
        });
    }

    private static void ConfigureProcessedStripeEventEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcessedStripeEvent>(entity =>
        {
            entity.HasIndex(e => e.EventId).IsUnique();
            entity.Property(e => e.EventId).IsRequired().HasMaxLength(255);
        });
    }

    private static void ConfigureProcessedRequestEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcessedRequest>(entity =>
        {
            entity.HasIndex(request => new
            {
                request.UserId,
                request.IdempotencyKey,
                request.RequestType,
                request.RequestOrdinal
            }).IsUnique();
            entity.HasIndex(request => request.CreatedAtUtc);
            entity.Property(request => request.IdempotencyKey).IsRequired().HasMaxLength(200);
            entity.Property(request => request.RequestType).IsRequired().HasMaxLength(256);
            entity.HasOne<User>().WithMany().HasForeignKey(request => request.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureAiUsageDailyEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AiUsageDaily>(entity =>
        {
            entity.HasIndex(u => new { u.Date, u.Model, u.Purpose, u.UserId })
                .IsUnique()
                .AreNullsDistinct(false);
            entity.Property(u => u.Model).HasMaxLength(64);
            entity.Property(u => u.Purpose).HasMaxLength(64);
            entity.Property(u => u.CostUsd).HasColumnType("numeric");
        });
    }

    private static void ConfigureNotificationEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Notification>(entity =>
        {
            entity.HasIndex(n => new { n.UserId, n.IsRead });
            entity.HasIndex(n => new { n.UserId, n.CreatedAtUtc }).IsDescending(false, true);
            entity.HasIndex(n => n.Url).HasFilter("\"Url\" IS NOT NULL");
            entity.HasIndex(n => n.DedupeKey).IsUnique().HasFilter("\"DedupeKey\" IS NOT NULL");
            entity.HasIndex(n => new { n.UserId, n.IsDeleted });
            entity.HasIndex(n => new { n.UserId, n.UpdatedAtUtc });
            entity.HasQueryFilter(n => !n.IsDeleted);
        });
    }

    private static void ConfigureReferralEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Referral>(entity =>
        {
            entity.HasIndex(r => r.ReferrerId);
            entity.HasIndex(r => r.ReferredUserId).IsUnique();
            entity.Property(r => r.ReferrerReward).HasDefaultValue(ReferralRewardKind.None);
            entity.Property(r => r.ReferredReward).HasDefaultValue(ReferralRewardKind.None);
        });
    }

    private static void ConfigureUserAchievementEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserAchievement>(entity =>
        {
            entity.HasIndex(ua => new { ua.UserId, ua.AchievementId }).IsUnique();
            entity.HasIndex(ua => ua.UserId);
            entity.Property(ua => ua.AchievementId).HasMaxLength(50);
        });
    }

    private static void ConfigureStreakFreezeEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StreakFreeze>(entity =>
        {
            entity.HasIndex(sf => new { sf.UserId, sf.UsedOnDate }).IsUnique();
            entity.Property(sf => sf.Origin).HasConversion<string>().HasMaxLength(32);
            entity.HasOne<User>().WithMany().HasForeignKey(sf => sf.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureXpAwardLogEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<XpAwardLog>(entity =>
        {
            entity.HasIndex(x => new { x.UserId, x.AwardedAtUtc });
            entity.Property(x => x.Source).HasConversion<string>().HasMaxLength(32);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureUserSessionEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserSession>(entity =>
        {
            entity.HasIndex(s => s.TokenHash).IsUnique();
            entity.HasIndex(s => s.UserId);
            entity.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(s => s.TokenHash).HasMaxLength(128);
        });
    }

    private static void ConfigureUserSessionRefreshTokenEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserSessionRefreshToken>(entity =>
        {
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.HasIndex(token => token.UserSessionId);
            entity.HasOne<UserSession>().WithMany().HasForeignKey(token => token.UserSessionId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(token => token.TokenHash).HasMaxLength(128);
        });
    }

    private static void ConfigureApiKeyEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.HasIndex(k => k.KeyPrefix);
            entity.HasIndex(k => k.UserId);
            entity.HasIndex(k => k.OAuthFamilyId);
            entity.Property(k => k.IsRevoked).IsConcurrencyToken();
            entity.HasOne<User>().WithMany().HasForeignKey(k => k.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(k => k.Name).HasMaxLength(50);
            entity.Property(k => k.OAuthClientId).HasMaxLength(OAuthClientMetadata.MaxClientIdLength);
            entity.Property(k => k.KeyPrefix).HasMaxLength(12);
            entity.Property(k => k.Scopes)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>())
                .HasColumnType(JsonbColumnType)
                .HasDefaultValueSql(EmptyJsonArraySql)
                .Metadata.SetValueComparer(CreateReadOnlyListComparer<string>());
        });
    }

    private static void ConfigurePendingAgentOperationEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PendingAgentOperationState>(entity =>
        {
            entity.HasIndex(item => new { item.UserId, item.CapabilityId });
            entity.HasIndex(item => new { item.UserId, item.OperationFingerprint });
            entity.Property(item => item.CapabilityId).HasMaxLength(100);
            entity.Property(item => item.OperationId).HasMaxLength(100);
            entity.Property(item => item.ArgumentsJson).HasColumnType(JsonbColumnType);
            entity.Property(item => item.DisplayName).HasMaxLength(200);
            entity.Property(item => item.Summary).HasMaxLength(500);
            entity.Property(item => item.OperationFingerprint).HasMaxLength(256);
            entity.Property(item => item.PreviewFingerprint).HasMaxLength(256);
            entity.Property(item => item.ArgumentSchemaVersion).IsRequired();
            entity.Property(item => item.RevisionNumber).IsConcurrencyToken();
            entity.Property(item => item.ConfirmationTokenHash).HasMaxLength(64);
            entity.Property(item => item.ConsumedAtUtc).IsConcurrencyToken();
            entity.Property(item => item.Surface).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.RiskClass).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.ConfirmationRequirement).HasConversion<string>().HasMaxLength(32);
        });
    }

    private static void ConfigureAgentStepUpChallengeEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentStepUpChallengeState>(entity =>
        {
            entity.HasIndex(item => new { item.UserId, item.PendingOperationId, item.CreatedAtUtc });
            entity.Property(item => item.CodeHash).HasMaxLength(64);
        });
    }

    private static void ConfigurePendingClarificationEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PendingClarification>(entity =>
        {
            entity.HasIndex(item => new { item.UserId, item.CreatedAtUtc });
            entity.HasIndex(item => item.ExpiresAtUtc);
            entity.Property(item => item.ToolName).HasMaxLength(100);
            entity.Property(item => item.MissingArgumentKey).HasMaxLength(100);
            entity.Property(item => item.Question).HasMaxLength(500);
            entity.Property(item => item.PartialArgumentsJson).HasColumnType(JsonbColumnType);
            entity.Property(item => item.QuickActionsJson).HasColumnType(JsonbColumnType);
            entity.Property(item => item.ResolvedAtUtc).HasField("_resolvedAtUtc");
            entity.HasOne<User>().WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureAgentAuditLogEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentAuditLog>(entity =>
        {
            entity.HasIndex(item => new { item.UserId, item.CreatedAtUtc });
            entity.HasIndex(item => new { item.CapabilityId, item.CreatedAtUtc });
            entity.Property(item => item.CapabilityId).HasMaxLength(100);
            entity.Property(item => item.SourceName).HasMaxLength(100);
            entity.Property(item => item.CorrelationId).HasMaxLength(100);
            entity.Property(item => item.TargetId).HasMaxLength(100);
            entity.Property(item => item.TargetName).HasMaxLength(200);
            entity.Property(item => item.RedactedArguments).HasMaxLength(4000);
            entity.Property(item => item.Error).HasMaxLength(500);
            entity.Property(item => item.ShadowReason).HasMaxLength(500);
            entity.Property(item => item.Surface).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.AuthMethod).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.RiskClass).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.PolicyDecision).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.OutcomeStatus).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.ShadowPolicyDecision).HasConversion<string>().HasMaxLength(32);
        });
    }

    private static void ConfigureDistributedRateLimitBucketEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DistributedRateLimitBucket>(entity =>
        {
            entity.HasIndex(item => new { item.PolicyName, item.PartitionKey, item.WindowStartUtc }).IsUnique();
            entity.Property(item => item.PolicyName).HasMaxLength(64);
            entity.Property(item => item.PartitionKey).HasMaxLength(256);
        });
    }

    private static void ConfigureAppConfigEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppConfig>(entity =>
        {
            entity.HasKey(c => c.Key);
            entity.Property(c => c.Key).HasMaxLength(100);
            entity.Property(c => c.Value).HasMaxLength(500).IsRequired();
            entity.Property(c => c.Description).HasMaxLength(500);

            entity.HasData(
                AppConfig.Create("MaxHabitDepth", "5", "Maximum nesting depth for sub-habits"),
                AppConfig.Create("MaxTagsPerHabit", "5", "Maximum number of tags per habit"),
                AppConfig.Create("ReferralRewardDays", "10", "Days of Pro added per successful referral"),
                AppConfig.Create("MaxReferrals", "10", "Maximum successful referrals per user"),
                AppConfig.Create("MinSupportedVersion", "1.3.11", "Minimum supported client app version; clients below this receive HTTP 426"),
                AppConfig.Create("FreeAiCallsPerDay", "45", "Daily AI call safety budget for Free users"),
                AppConfig.Create("ProAiCallsPerDay", "450", "Daily AI call safety budget for Pro users"),
                AppConfig.Create("FreeAiMicroDollarsPerDay", "20000", "Daily AI safety budget for Free users in USD micro-dollars"),
                AppConfig.Create("ProAiMicroDollarsPerDay", "120000", "Daily AI safety budget for Pro users in USD micro-dollars"),
                AppConfig.Create("RequireApiKeyCreationStepUp", "false", "Turn on once a client build carrying the API key creation challenge flow is live in the Play fleet"));
        });
    }

    private static void ConfigureNotionEntities(ModelBuilder modelBuilder,
        EncryptionValueConverter? encConverter, NullableEncryptionValueConverter? nullableEncConverter)
    {
        modelBuilder.Entity<NotionConnection>(entity =>
        {
            entity.HasIndex(c => c.UserId).IsUnique();
            entity.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(c => c.Status).HasConversion<string>().HasMaxLength(32);
            if (encConverter is not null) entity.Property(c => c.AccessToken).HasConversion(encConverter);
            if (nullableEncConverter is not null) entity.Property(c => c.RefreshToken).HasConversion(nullableEncConverter);
        });
        modelBuilder.Entity<NotionTask>(entity =>
        {
            entity.HasIndex(t => new { t.UserId, t.PageId }).IsUnique();
            entity.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(t => t.Title).HasMaxLength(NotionTask.MaxTitleLength);
        });
        modelBuilder.Entity<NotionOAuthState>(entity =>
        {
            entity.HasIndex(s => s.StateHash).IsUnique();
            entity.HasIndex(s => s.ExpiresAtUtc);
            entity.Property(s => s.StateHash).HasMaxLength(64);
            entity.Property(s => s.RedirectUri).HasMaxLength(2048);
            entity.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureChecklistTemplateEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChecklistTemplate>(entity =>
        {
            entity.HasIndex(ct => ct.UserId);
            entity.HasIndex(ct => new { ct.UserId, ct.IsDeleted });
            entity.HasIndex(ct => new { ct.UserId, ct.UpdatedAtUtc });
            entity.HasQueryFilter(ct => !ct.IsDeleted);
            entity.HasOne<User>().WithMany().HasForeignKey(ct => ct.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(ct => ct.Name).HasMaxLength(100);
            entity.Property(ct => ct.Items)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>())
                .HasColumnType(JsonbColumnType)
                .HasDefaultValueSql(EmptyJsonArraySql)
                .Metadata.SetValueComparer(
                    new ValueComparer<IReadOnlyList<string>>(
                        (l1, l2) => JsonSerializer.Serialize(l1, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(l2, (JsonSerializerOptions?)null),
                        c => JsonSerializer.Serialize(c, (JsonSerializerOptions?)null).GetHashCode(),
                        c => JsonSerializer.Deserialize<List<string>>(JsonSerializer.Serialize(c, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null)!));
        });
    }

    private static void ConfigureAppFeatureFlagEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppFeatureFlag>(entity =>
        {
            entity.HasKey(f => f.Key);
            entity.Property(f => f.Key).HasMaxLength(100);
            entity.Property(f => f.Description).HasMaxLength(500);
            entity.Property(f => f.PlanRequirement).HasMaxLength(50);

            entity.HasData(
                new { Key = "astra_block_first", Enabled = false, PlanRequirement = (string?)null, Description = (string?)"Compose negotiated Astra answers from successful typed results", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "astra_eval_trace", Enabled = false, PlanRequirement = (string?)null, Description = (string?)"Opt-in tool-call traces for Astra evaluation accounts", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "redesign", Enabled = false, PlanRequirement = (string?)null, Description = (string?)"Serve the redesigned web and Android builds to this account", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "offline_mode", Enabled = true, PlanRequirement = (string?)null, Description = (string?)"Enable offline mode with background sync", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "astra_conversation_recording", Enabled = false, PlanRequirement = (string?)null, Description = (string?)"Record encrypted Astra conversations for failure diagnosis for 30 days after privacy disclosure is published", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "ai_chat", Enabled = true, PlanRequirement = (string?)"Free", Description = (string?)"AI chat assistant", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "ai_summary", Enabled = true, PlanRequirement = (string?)"Pro", Description = (string?)"AI daily summary", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "ai_retrospective", Enabled = true, PlanRequirement = (string?)"Pro", Description = (string?)"AI retrospective analysis", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "sub_habits", Enabled = true, PlanRequirement = (string?)null, Description = (string?)"Sub-habit nesting", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "goal_tracking", Enabled = true, PlanRequirement = (string?)null, Description = (string?)"Goal tracking with progress", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "push_notifications", Enabled = true, PlanRequirement = (string?)null, Description = (string?)"Push notification reminders", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "scheduled_reminders", Enabled = true, PlanRequirement = (string?)null, Description = (string?)"Custom scheduled reminders", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "slip_alerts", Enabled = true, PlanRequirement = (string?)"Pro", Description = (string?)"Slip detection alerts", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "checklist_templates", Enabled = true, PlanRequirement = (string?)null, Description = (string?)"Reusable checklist templates", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "habit_duplication", Enabled = true, PlanRequirement = (string?)null, Description = (string?)"Duplicate habits", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "bulk_operations", Enabled = true, PlanRequirement = (string?)null, Description = (string?)"Bulk create/delete/log habits", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "calendar_integration", Enabled = true, PlanRequirement = (string?)"Pro", Description = (string?)"Google Calendar integration", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "notion_integration", Enabled = false, PlanRequirement = (string?)"Pro", Description = (string?)"Notion task integration", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "api_keys", Enabled = true, PlanRequirement = (string?)"Pro", Description = (string?)"Personal API keys", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) });
        });
    }

    private static void ConfigureContentBlockEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ContentBlock>(entity =>
        {
            entity.HasIndex(cb => new { cb.Key, cb.Locale }).IsUnique();
            entity.Property(cb => cb.Key).HasMaxLength(100);
            entity.Property(cb => cb.Locale).HasMaxLength(10);
            entity.Property(cb => cb.Category).HasMaxLength(50);
        });
    }

    private static void ConfigureUserEntity(ModelBuilder modelBuilder, NullableEncryptionValueConverter? nullableEncConverter)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasQueryFilter(u => !u.IsDeactivated);

            entity.HasIndex(u => u.Email).IsUnique();
            entity.HasIndex(u => u.ReferralCode).IsUnique().HasFilter("\"ReferralCode\" IS NOT NULL");
            entity.Property(u => u.AppleSubject).HasMaxLength(255);
            entity.Property(u => u.AppleTokenClientId).HasMaxLength(255);
            entity.Property(u => u.AppleRefreshToken).HasColumnType("text");
            entity.HasIndex(u => u.AppleSubject).IsUnique().HasFilter("\"AppleSubject\" IS NOT NULL");
            entity.Property(u => u.AppStoreOriginalTransactionId).HasMaxLength(255);
            entity.Property(u => u.AppStoreEnvironment).HasMaxLength(20);
            entity.HasIndex(u => u.AppStoreOriginalTransactionId).IsUnique().HasFilter("\"AppStoreOriginalTransactionId\" IS NOT NULL");
            entity.HasIndex(u => u.PlayPurchaseToken).IsUnique().HasFilter("\"PlayPurchaseToken\" IS NOT NULL");
            entity.HasIndex(u => u.PublicProfileSlug).IsUnique().HasFilter("\"PublicProfileSlug\" IS NOT NULL");

            entity.Property(u => u.StripeCoverageExpiresAtUtc);
            entity.Property(u => u.StripeCoverageInterval);
            entity.Property(u => u.StripeCoverageLapseReason).HasConversion<string>().HasMaxLength(32);
            entity.Property(u => u.PlayCoverageExpiresAtUtc);
            entity.Property(u => u.PlayCoverageInterval);
            entity.Property(u => u.PlayCoverageLapseReason).HasConversion<string>().HasMaxLength(32);
            entity.Property(u => u.AppStoreCoverageExpiresAtUtc);
            entity.Property(u => u.AppStoreCoverageInterval);
            entity.Property(u => u.AppStoreCoverageLapseReason).HasConversion<string>().HasMaxLength(32);

            entity.Property(u => u.SubscriptionPlan).HasColumnName("Plan");
            entity.Property(u => u.SubscriptionExpiresAtUtc).HasColumnName("PlanExpiresAt");
            entity.Property(u => u.StripeRenewalStoppedByDeletion).HasDefaultValue(false);
            entity.Property(u => u.PlayRenewalStoppedByDeletion).HasDefaultValue(false);
            entity.Property(u => u.ProactiveAstraEnabled).HasDefaultValue(true).ValueGeneratedNever();
            entity.Property(u => u.PublicProfileSlug).HasMaxLength(32);

            entity.Property(u => u.ColorScheme).HasMaxLength(50);
            entity.Property(u => u.Language).HasMaxLength(10);
            entity.Property(u => u.SignupLanguage).HasMaxLength(10);
            entity.Property(u => u.ThemePreference).HasMaxLength(10);
            entity.Property(u => u.TimeZone).HasMaxLength(100);

            entity.Property(u => u.GoogleCalendarAutoSyncStatus)
                .HasConversion<string>()
                .HasMaxLength(32);

            entity.Property(u => u.SubscriptionLapseReason)
                .HasConversion<string>()
                .HasMaxLength(32);

            entity.Property(u => u.GoogleCalendarLastSyncError).HasMaxLength(500);

            entity.Property(u => u.GoogleCalendarSelectedIds).HasColumnType("text");

            entity.HasIndex(u => new { u.GoogleCalendarAutoSyncEnabled, u.GoogleCalendarLastSyncedAt })
                .HasFilter("\"GoogleCalendarAutoSyncEnabled\" = TRUE");

            if (nullableEncConverter is null)
                return;

            entity.Property(u => u.GoogleAccessToken).HasConversion(nullableEncConverter).HasColumnType("text");
            entity.Property(u => u.GoogleRefreshToken).HasConversion(nullableEncConverter).HasColumnType("text");
            entity.Property(u => u.AppleRefreshToken).HasConversion(nullableEncConverter).HasColumnType("text");
        });
    }

    private static void ConfigureGoogleCalendarSyncSuggestionEntity(
        ModelBuilder modelBuilder,
        EncryptionValueConverter? encConverter,
        NullableEncryptionValueConverter? nullableEncConverter)
    {
        modelBuilder.Entity<GoogleCalendarSyncSuggestion>(entity =>
        {
            entity.HasIndex(s => s.UserId);
            entity.HasIndex(s => new { s.UserId, s.GoogleEventId }).IsUnique();
            entity.HasIndex(s => new { s.UserId, s.DismissedAtUtc, s.ImportedAtUtc });

            entity.Property(s => s.GoogleEventId).HasMaxLength(1024).IsRequired();

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            if (encConverter is null || nullableEncConverter is null)
                return;

            entity.Property(s => s.Title).HasConversion(encConverter).HasColumnType("text");
            entity.Property(s => s.RawEventJson).HasConversion(encConverter).HasColumnType("text");
        });
    }

    private static void ConfigureHabitEntity(
        ModelBuilder modelBuilder,
        bool usePostgresArrayColumns,
        EncryptionValueConverter? encConverter,
        NullableEncryptionValueConverter? nullableEncConverter)
    {
        modelBuilder.Entity<Habit>(entity =>
        {
            entity.HasIndex(h => h.UserId);
            entity.HasIndex(h => new { h.UserId, h.IsDeleted });
            entity.HasIndex(h => new { h.UserId, h.UpdatedAtUtc });
            entity.HasQueryFilter(h => !h.IsDeleted);

            entity.HasOne<User>().WithMany().HasForeignKey(h => h.UserId).OnDelete(DeleteBehavior.Cascade);

            entity.Property(h => h.GoogleEventId).HasMaxLength(1024);
            entity.Property(h => h.IntervalWeeks).HasColumnType("integer");
            entity.Property(h => h.Schedule).HasConversion<int>().HasDefaultValue(HabitSchedule.OneTime);
            entity.Property(h => h.StartDate).HasDefaultValueSql("'0001-01-01'");
            entity.Property(h => h.NextDueDate).HasDefaultValueSql("'0001-01-01'");
            entity.HasIndex(h => new { h.UserId, h.GoogleEventId })
                .HasFilter("\"GoogleEventId\" IS NOT NULL AND \"IsDeleted\" = FALSE")
                .IsUnique();

            entity.HasMany(h => h.Logs)
                .WithOne()
                .HasForeignKey(l => l.HabitId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(h => h.Children)
                .WithOne()
                .HasForeignKey(h => h.ParentHabitId)
                .OnDelete(DeleteBehavior.Cascade);

            ConfigureHabitDaysProperty(entity, usePostgresArrayColumns);

            entity.Property(h => h.ChecklistItems)
                .HasConversion(
                    v => SerializeJson(v),
                    v => DeserializeJson(v, new List<ChecklistItem>()))
                .HasColumnType(JsonbColumnType)
                .HasDefaultValueSql(EmptyJsonArraySql)
                .Metadata.SetValueComparer(CreateReadOnlyListComparer<ChecklistItem>());

            entity.Property(h => h.ReminderTimes)
                .HasConversion(
                    v => SerializeJson(v),
                    v => DeserializeJson(v, new List<int> { 15 }))
                .HasColumnType(JsonbColumnType)
                .HasDefaultValueSql("'[15]'::jsonb")
                .Metadata.SetValueComparer(CreateReadOnlyListComparer<int>());

            entity.Property(h => h.ScheduledReminders)
                .HasConversion(
                    v => SerializeJson(v),
                    v => DeserializeJson(v, new List<ScheduledReminderTime>()))
                .HasColumnType(JsonbColumnType)
                .HasDefaultValueSql(EmptyJsonArraySql)
                .Metadata.SetValueComparer(CreateReadOnlyListComparer<ScheduledReminderTime>());

            entity.Property(h => h.RelativeReminders)
                .HasConversion(
                    v => SerializeJson(v),
                    v => DeserializeJson(v, new List<RelativeReminderTime>()))
                .HasColumnType(JsonbColumnType)
                .HasDefaultValueSql(EmptyJsonArraySql)
                .Metadata.SetValueComparer(CreateReadOnlyListComparer<RelativeReminderTime>());

            if (encConverter is null || nullableEncConverter is null)
                return;

            entity.Property(h => h.Title).HasConversion(encConverter).HasColumnType("text");
            entity.Property(h => h.Description).HasConversion(nullableEncConverter).HasColumnType("text");
            entity.Property(h => h.Emoji).HasConversion(nullableEncConverter).HasColumnType("text");
        });
    }

    private static void ConfigureHabitDaysProperty(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Habit> entity,
        bool usePostgresArrayColumns)
    {
        var property = entity.Property(h => h.Days);

        if (usePostgresArrayColumns)
        {
            property.HasConversion(
                    v => v.ToList(),
                    v => v.ToList())
                .HasColumnType("text[]");
        }
        else
        {
            property.HasConversion(
                    v => SerializeDays(v),
                    v => DeserializeDays(v))
                .HasColumnType("text");
        }

        property.Metadata.SetValueComparer(CreateDayOfWeekCollectionComparer());
    }

    private static void ConfigureHabitLogEntity(ModelBuilder modelBuilder, NullableEncryptionValueConverter? nullableEncConverter)
    {
        modelBuilder.Entity<HabitLog>(entity =>
        {
            entity.Property(l => l.CompletionOrdinal).HasDefaultValue(0);
            entity.HasIndex(l => new { l.HabitId, l.Date }, "IX_HabitLogs_HabitId_Date");

            entity.HasIndex(l => new { l.HabitId, l.Date, l.CompletionOrdinal }, "IX_HabitLogs_HabitId_Date_Completed")
                .HasFilter("\"Value\" > 0 AND NOT \"IsDeleted\"")
                .IsUnique();

            entity.HasIndex(l => new { l.HabitId, l.UpdatedAtUtc });

            entity.HasQueryFilter(l => !l.IsDeleted);

            if (nullableEncConverter is null)
                return;

            entity.Property(l => l.Note).HasConversion(nullableEncConverter).HasColumnType("text").HasMaxLength(1000);
        });
    }

    private static void ConfigureGoalEntity(
        ModelBuilder modelBuilder,
        EncryptionValueConverter? encConverter,
        NullableEncryptionValueConverter? nullableEncConverter)
    {
        modelBuilder.Entity<Goal>(entity =>
        {
            entity.Ignore(g => g.HasActiveLinkedHabits);
            entity.Ignore(g => g.IsProgressDerived);
            entity.HasIndex(g => g.UserId);
            entity.HasIndex(g => new { g.UserId, g.IsDeleted });
            entity.HasIndex(g => new { g.UserId, g.UpdatedAtUtc });
            entity.HasQueryFilter(g => !g.IsDeleted);

            entity.Property(g => g.Unit).HasMaxLength(50);

            entity.HasMany(g => g.ProgressLogs)
                .WithOne()
                .HasForeignKey(l => l.GoalId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(g => g.Habits)
                .WithMany(h => h.Goals)
                .UsingEntity("HabitGoals",
                    l => l.HasOne(typeof(Habit)).WithMany().HasForeignKey(nameof(Habit) + nameof(Habit.Id)).OnDelete(DeleteBehavior.Cascade),
                    r => r.HasOne(typeof(Goal)).WithMany().HasForeignKey(nameof(Goal) + nameof(Goal.Id)).OnDelete(DeleteBehavior.Cascade));

            if (encConverter is null || nullableEncConverter is null)
                return;

            entity.Property(g => g.Title).HasConversion(encConverter).HasColumnType("text").HasMaxLength(200);
            entity.Property(g => g.Description).HasConversion(nullableEncConverter).HasColumnType("text");
        });
    }

    private static void ConfigureGoalProgressLogEntity(ModelBuilder modelBuilder, NullableEncryptionValueConverter? nullableEncConverter)
    {
        modelBuilder.Entity<GoalProgressLog>(entity =>
        {
            entity.HasIndex(l => l.GoalId);
            entity.HasIndex(l => new { l.GoalId, l.IsDeleted });
            entity.HasIndex(l => new { l.GoalId, l.UpdatedAtUtc });
            entity.HasQueryFilter(l => !l.IsDeleted);

            if (nullableEncConverter is null)
                return;

            entity.Property(l => l.Note).HasConversion(nullableEncConverter).HasColumnType("text");
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var modifiedHabits = ChangeTracker.Entries<Habit>()
            .Where(entry => entry.State == EntityState.Modified).ToArray();
        if (modifiedHabits.Length == 0 || !Database.IsRelational())
        {
            foreach (var entry in modifiedHabits)
                entry.Property(h => h.ReminderProbeVersion).CurrentValue++;
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        var habitIds = modifiedHabits.Select(entry => entry.Entity.Id).Distinct().ToArray();
        foreach (var entry in modifiedHabits)
            entry.Property(h => h.ReminderProbeVersion).IsModified = false;

        if (Database.CurrentTransaction is not null)
            return SaveHabitChanges(acceptAllChangesOnSuccess, habitIds);

        return Database.CreateExecutionStrategy().Execute(() =>
        {
            using var transaction = Database.BeginTransaction();
            var saved = SaveHabitChanges(false, habitIds);
            transaction.Commit();
            if (acceptAllChangesOnSuccess)
                ChangeTracker.AcceptAllChanges();
            return saved;
        });
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<ITimestamped>()
            .Where(e => e.State is EntityState.Modified or EntityState.Added))
        {
            entry.Entity.UpdatedAtUtc = now;
        }

        var modifiedHabits = ChangeTracker.Entries<Habit>()
            .Where(entry => entry.State == EntityState.Modified).ToArray();
        if (modifiedHabits.Length == 0 || !Database.IsRelational())
        {
            foreach (var entry in modifiedHabits)
                entry.Property(h => h.ReminderProbeVersion).CurrentValue++;
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        var habitIds = modifiedHabits.Select(entry => entry.Entity.Id).Distinct().ToArray();
        foreach (var entry in modifiedHabits)
            entry.Property(h => h.ReminderProbeVersion).IsModified = false;

        if (Database.CurrentTransaction is not null)
            return await SaveHabitChangesAsync(acceptAllChangesOnSuccess, habitIds, cancellationToken);

        return await Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
            var saved = await SaveHabitChangesAsync(false, habitIds, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (acceptAllChangesOnSuccess)
                ChangeTracker.AcceptAllChanges();
            return saved;
        });
    }

    private int SaveHabitChanges(bool acceptAllChangesOnSuccess, Guid[] habitIds)
    {
        var saved = base.SaveChanges(false);
        Habits.IgnoreQueryFilters().Where(habit => habitIds.Contains(habit.Id))
            .ExecuteUpdate(setters => setters.SetProperty(habit => habit.ReminderProbeVersion,
                habit => habit.ReminderProbeVersion + 1));
        if (acceptAllChangesOnSuccess)
            ChangeTracker.AcceptAllChanges();
        return saved;
    }

    private async Task<int> SaveHabitChangesAsync(bool acceptAllChangesOnSuccess, Guid[] habitIds,
        CancellationToken cancellationToken)
    {
        var saved = await base.SaveChangesAsync(false, cancellationToken);
        await Habits.IgnoreQueryFilters().Where(habit => habitIds.Contains(habit.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(habit => habit.ReminderProbeVersion,
                habit => habit.ReminderProbeVersion + 1), cancellationToken);
        if (acceptAllChangesOnSuccess)
            ChangeTracker.AcceptAllChanges();
        return saved;
    }

    private static string SerializeDays(ICollection<System.DayOfWeek> days)
    {
        return JsonSerializer.Serialize(days.Select(day => (int)day).ToList(), (JsonSerializerOptions?)null);
    }

    private static string SerializeJson<T>(T value)
    {
        return JsonSerializer.Serialize(value, (JsonSerializerOptions?)null);
    }

    private static List<T> DeserializeJson<T>(string value, List<T> fallback)
    {
        return JsonSerializer.Deserialize<List<T>>(value, (JsonSerializerOptions?)null) ?? fallback;
    }

    private static List<System.DayOfWeek> DeserializeDays(string value)
    {
        var days = JsonSerializer.Deserialize<List<int>>(value, (JsonSerializerOptions?)null);
        return days?.Select(day => (System.DayOfWeek)day).ToList() ?? new List<System.DayOfWeek>();
    }

    private static ValueComparer<ICollection<System.DayOfWeek>> CreateDayOfWeekCollectionComparer()
    {
        return new ValueComparer<ICollection<System.DayOfWeek>>(
            (left, right) => left!.SequenceEqual(right!),
            collection => collection.Aggregate(0, (hash, day) => HashCode.Combine(hash, day.GetHashCode())),
            collection => collection.ToList());
    }

    private static ValueComparer<IReadOnlyList<T>> CreateReadOnlyListComparer<T>()
    {
        return new ValueComparer<IReadOnlyList<T>>(
            (left, right) => SerializeJson(left) == SerializeJson(right),
            collection => SerializeJson(collection).GetHashCode(),
            collection => DeserializeJson(SerializeJson(collection), new List<T>()));
    }
}
