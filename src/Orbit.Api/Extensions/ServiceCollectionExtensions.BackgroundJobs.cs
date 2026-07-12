using Hangfire;
using Hangfire.PostgreSql;
using Orbit.Application.Auth.Jobs;
using Orbit.Application.Marketing.Jobs;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Api.Extensions;

public static partial class ServiceCollectionExtensions
{
    private static void AddBackgroundServices(WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<ISlipAlertMessageService, AiSlipAlertMessageService>();
        builder.Services.AddScoped<IProactiveCheckinMessageService, AiProactiveCheckinMessageService>();

        if (!BuildTimeDocumentGeneration.IsActive)
        {
            var useDurableQueue = builder.Configuration.GetSection(BackgroundJobSettings.SectionName)
                .Get<BackgroundJobSettings>()?.UseDurableQueue ?? false;

            builder.Services.AddHostedService<DataEncryptionMigrationService>();
            builder.Services.AddHostedService<XpAwardLogBackfillHostedService>();

            AddDurableJobQueue(builder);

            if (useDurableQueue)
                AddDurableRecurringJobs(builder);
            else
                AddInProcessSchedulers(builder);
        }

        builder.Services.AddHealthChecks()
            .AddCheck<BackgroundServiceHealthCheck>("background-services");
    }

    private static void AddInProcessSchedulers(WebApplicationBuilder builder)
    {
        builder.Services.AddHostedService<ReminderSchedulerService>();
        builder.Services.AddHostedService<GoalDeadlineNotificationService>();
        builder.Services.AddHostedService<SlipAlertSchedulerService>();
        builder.Services.AddHostedService<ProactiveCheckinSchedulerService>();
        builder.Services.AddHostedService<AccountDeletionService>();
        builder.Services.AddHostedService<HabitDueDateAdvancementService>();
        builder.Services.AddHostedService<StreakGoalSyncService>();
        builder.Services.AddHostedService<StreakFreezeAutoActivationService>();
        builder.Services.AddHostedService<SyncCleanupService>();
        builder.Services.AddHostedService<PlayNotificationCleanupService>();
        builder.Services.AddHostedService<CalendarAutoSyncService>();
        builder.Services.AddHostedService<OpenAiBatchPollerService>();
        builder.Services.AddHostedService<AiUsageSummaryService>();
    }

    /// <summary>
    /// Registers the durable Hangfire job queue — PostgreSQL-backed storage, the enqueue client, and a
    /// processing server — unconditionally, so request-path work such as the verification-code email can
    /// be handed to <see cref="IBackgroundJobClient"/> and dispatched out of band: the send survives a
    /// restart and is auto-retried on failure. This is independent of
    /// <c>BackgroundServices:UseDurableQueue</c>, which only governs whether the recurring scans run as
    /// Hangfire recurring jobs or as in-process polling loops.
    /// </summary>
    private static void AddDurableJobQueue(WebApplicationBuilder builder)
    {
        var connectionString = OrbitConnectionStringFactory.ForSession(builder.Configuration);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "A database connection string is required for the durable background-job queue.");

        builder.Services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(postgres => postgres.UseNpgsqlConnection(connectionString)));
        builder.Services.AddHangfireServer(options =>
        {
            options.WorkerCount = 2;
            options.SchedulePollingInterval = TimeSpan.FromMinutes(1);
        });

        builder.Services.AddScoped<SendVerificationCodeEmailJob>();
        builder.Services.AddScoped<SendAccountDeletionCodeEmailJob>();
        builder.Services.AddScoped<SendMarketingEmailJob>();
    }

    private static void AddDurableRecurringJobs(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<ScheduledJobRunner>();
        AddScheduledJob<ReminderSchedulerService>(builder);
        AddScheduledJob<GoalDeadlineNotificationService>(builder);
        AddScheduledJob<SlipAlertSchedulerService>(builder);
        AddScheduledJob<ProactiveCheckinSchedulerService>(builder);
        AddScheduledJob<AccountDeletionService>(builder);
        AddScheduledJob<HabitDueDateAdvancementService>(builder);
        AddScheduledJob<StreakGoalSyncService>(builder);
        AddScheduledJob<StreakFreezeAutoActivationService>(builder);
        AddScheduledJob<SyncCleanupService>(builder);
        AddScheduledJob<PlayNotificationCleanupService>(builder);
        AddScheduledJob<CalendarAutoSyncService>(builder);
        AddScheduledJob<OpenAiBatchPollerService>(builder);
        AddScheduledJob<AiUsageSummaryService>(builder);

        builder.Services.AddHostedService<HangfireRecurringJobRegistrar>();
    }

    private static void AddScheduledJob<TJob>(WebApplicationBuilder builder)
        where TJob : class, IScheduledJob
    {
        builder.Services.AddSingleton<TJob>();
        builder.Services.AddSingleton<IScheduledJob>(sp => sp.GetRequiredService<TJob>());
    }
}
