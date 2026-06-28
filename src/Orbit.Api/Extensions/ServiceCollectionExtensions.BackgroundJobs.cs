using Hangfire;
using Hangfire.PostgreSql;
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

        var useDurableQueue = builder.Configuration.GetSection(BackgroundJobSettings.SectionName)
            .Get<BackgroundJobSettings>()?.UseDurableQueue ?? false;

        builder.Services.AddHostedService<DataEncryptionMigrationService>();
        builder.Services.AddHostedService<XpAwardLogBackfillHostedService>();

        if (useDurableQueue)
            AddDurableRecurringJobs(builder);
        else
            AddInProcessSchedulers(builder);

        builder.Services.AddHealthChecks()
            .AddCheck<BackgroundServiceHealthCheck>("background-services");
    }

    private static void AddInProcessSchedulers(WebApplicationBuilder builder)
    {
        builder.Services.AddHostedService<ReminderSchedulerService>();
        builder.Services.AddHostedService<GoalDeadlineNotificationService>();
        builder.Services.AddHostedService<SlipAlertSchedulerService>();
        builder.Services.AddHostedService<AccountDeletionService>();
        builder.Services.AddHostedService<HabitDueDateAdvancementService>();
        builder.Services.AddHostedService<StreakGoalSyncService>();
        builder.Services.AddHostedService<StreakFreezeAutoActivationService>();
        builder.Services.AddHostedService<SyncCleanupService>();
        builder.Services.AddHostedService<PlayNotificationCleanupService>();
        builder.Services.AddHostedService<CalendarAutoSyncService>();
        builder.Services.AddHostedService<OpenAiBatchPollerService>();
    }

    private static void AddDurableRecurringJobs(WebApplicationBuilder builder)
    {
        var connectionString = OrbitConnectionStringFactory.ForSession(builder.Configuration);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                $"{BackgroundJobSettings.SectionName}:UseDurableQueue is true but no database connection string is configured.");

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

        builder.Services.AddSingleton<ScheduledJobRunner>();
        AddScheduledJob<ReminderSchedulerService>(builder);
        AddScheduledJob<GoalDeadlineNotificationService>(builder);
        AddScheduledJob<SlipAlertSchedulerService>(builder);
        AddScheduledJob<AccountDeletionService>(builder);
        AddScheduledJob<HabitDueDateAdvancementService>(builder);
        AddScheduledJob<StreakGoalSyncService>(builder);
        AddScheduledJob<StreakFreezeAutoActivationService>(builder);
        AddScheduledJob<SyncCleanupService>(builder);
        AddScheduledJob<PlayNotificationCleanupService>(builder);
        AddScheduledJob<CalendarAutoSyncService>(builder);
        AddScheduledJob<OpenAiBatchPollerService>(builder);

        builder.Services.AddHostedService<HangfireRecurringJobRegistrar>();
    }

    private static void AddScheduledJob<TJob>(WebApplicationBuilder builder)
        where TJob : class, IScheduledJob
    {
        builder.Services.AddSingleton<TJob>();
        builder.Services.AddSingleton<IScheduledJob>(sp => sp.GetRequiredService<TJob>());
    }
}
