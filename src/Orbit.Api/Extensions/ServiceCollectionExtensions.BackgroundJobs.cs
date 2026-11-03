using Hangfire;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Hangfire.PostgreSql;
using Orbit.Application.Auth.Jobs;
using Orbit.Application.Gamification.Jobs;
using Orbit.Application.Marketing.Jobs;
using Orbit.Application.Referrals.Jobs;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Services.Notion;

namespace Orbit.Api.Extensions;

public static partial class ServiceCollectionExtensions
{
    private static void AddBackgroundServices(WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<ISlipAlertMessageService, AiSlipAlertMessageService>();
        builder.Services.AddScoped<IProactiveCheckinMessageService, AiProactiveCheckinMessageService>();
        builder.Services.AddScoped<Orbit.Application.Proactive.Services.IProactiveEngine, ProactiveEngineService>();

        builder.Services.AddScoped<Orbit.Application.Chat.IConversationTurnRecorder, ConversationTurnRecorder>();
        builder.Services.AddScoped<IConversationPlanStore, ConversationPlanStore>();

        if (!BuildTimeDocumentGeneration.IsActive)
        {
            builder.Services.AddHostedService<DataEncryptionMigrationService>();
            builder.Services.AddHostedService<XpAwardLogBackfillHostedService>();
            builder.Services.AddHostedService<BoundedReadStateBackfillHostedService>();

            AddDurableJobQueue(builder);
            builder.Services.TryAddSingleton<ScheduledJobRunner>();
            AddScheduledJob<ChatTurnRetentionJob>(builder);
            builder.Services.AddHostedService<HangfireRecurringJobRegistrar>();

            AddDurableRecurringJobs(builder);
        }

        builder.Services.TryAddSingleton<BackgroundServiceHealthCheck>();
        builder.Services.TryAddSingleton<IScheduledJobLeaseProvider, PostgresScheduledJobLeaseProvider>();
        builder.Services.AddHealthChecks()
            .AddCheck<BackgroundServiceHealthCheck>("background-services", tags: ["jobs"])
            .AddCheck<DatabaseHealthCheck>("database");
    }

    private static void AddDurableJobQueue(WebApplicationBuilder builder)
    {
        var connectionString = OrbitConnectionStringFactory.ForHangfire(builder.Configuration);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "A database connection string is required for the durable background-job queue.");

        builder.Services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(postgres => postgres.UseNpgsqlConnection(connectionString), new PostgreSqlStorageOptions
            {
                SchemaName = "hangfire",
                JobExpirationCheckInterval = TimeSpan.FromHours(1)
            })
            .WithJobExpirationTimeout(TimeSpan.FromDays(7)));
        builder.Services.AddHangfireServer(options =>
        {
            options.WorkerCount = 6;
            options.SchedulePollingInterval = TimeSpan.FromMinutes(1);
        });

        builder.Services.AddScoped<SendVerificationCodeEmailJob>();
        builder.Services.AddScoped<SendGamificationPushJob>();
        builder.Services.AddScoped<CompleteSignupJob>();
        builder.Services.AddScoped<SendWelcomeEmailJob>();
        builder.Services.AddScoped<SendAccountDeletionCodeEmailJob>();
        builder.Services.AddScoped<CancelSubscriptionOnDeletionJob>();
        builder.Services.AddScoped<RevokeAppleTokenOnDeletionJob>();
        builder.Services.AddScoped<OwedSubscriptionCancellationJob>();
        builder.Services.AddHangfireServer(options =>
        {
            options.Queues = ["marketing"];
            options.WorkerCount = 1;
        });
        builder.Services.AddScoped<SendMarketingBroadcastPageJob>();
        builder.Services.AddScoped<IMarketingBroadcastDeliveryStore, MarketingBroadcastDeliveryStore>();
        builder.Services.AddScoped<Orbit.Application.Waitlist.Commands.SendWaitlistConfirmationJob>();
        builder.Services.AddScoped<SendMarketingEmailJob>();
        builder.Services.AddScoped<ProactivePushJob>();
        builder.Services.AddScoped<DeliverReferralRewardJob>();
    }

    internal static void AddDurableRecurringJobs(WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton<ScheduledJobRunner>();
        AddScheduledJob<HangfireHistoryRetentionJob>(builder);
        AddScheduledJob<ReminderSchedulerService>(builder);
        AddScheduledJob<GoalDeadlineNotificationService>(builder);
        if (IsPeriodCloseNotificationEnabled(builder.Configuration))
            AddScheduledJob<PeriodCloseNotificationService>(builder);
        AddScheduledJob<SlipAlertSchedulerService>(builder);
        AddScheduledJob<ProactiveEngineSchedulerService>(builder);
        AddScheduledJob<AccountDeletionService>(builder);
        AddScheduledJob<OwedSubscriptionCancellationService>(builder);
        AddScheduledJob<HabitDueDateAdvancementService>(builder);
        if (IsStreakFreezeAutoActivationEnabled(builder.Configuration))
            AddScheduledJob<StreakFreezeAutoActivationService>(builder);
        AddScheduledJob<StreakGoalSyncService>(builder);
        AddScheduledJob<SyncCleanupService>(builder);
        AddScheduledJob<PlayNotificationCleanupService>(builder);
        AddScheduledJob<CalendarAutoSyncService>(builder);
        AddScheduledJob<NotionAutoSyncService>(builder);
        AddScheduledJob<AiUsageSummaryService>(builder);
        builder.Services.AddHostedService<AiUsageSummaryStartupService>();
        AddScheduledJob<FoundingAchievementReconciliationService>(builder);

    }

    private static void AddScheduledJob<TJob>(WebApplicationBuilder builder)
        where TJob : class, IScheduledJob
    {
        builder.Services.AddSingleton<TJob>();
        builder.Services.AddSingleton<IScheduledJob>(sp => sp.GetRequiredService<TJob>());
    }

    /// <summary>
    /// Reads the permanent operational kill switch for automatic streak coverage, which defaults to enabled.
    /// </summary>
    internal static bool IsStreakFreezeAutoActivationEnabled(IConfiguration configuration) =>
        configuration.GetValue("BackgroundServices:StreakFreezeAutoActivationEnabled", true);

    internal static bool IsPeriodCloseNotificationEnabled(IConfiguration configuration) =>
        configuration.GetValue("BackgroundServices:PeriodCloseNotificationEnabled", false);
}
