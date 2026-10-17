using System.Text;
using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Sentry;
using Sentry.AspNetCore;
using Orbit.Api.Authentication;
using Orbit.Api.Authorization;
using Orbit.Api.Idempotency;
using Orbit.Api.OAuth;
using Orbit.Api.Observability;
using Orbit.Api.RateLimiting;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Services;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Services;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Events;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Events;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Services.Calendar;
using PostHog;

namespace Orbit.Api.Extensions;

public static partial class ServiceCollectionExtensions
{
    public static WebApplicationBuilder ValidateOrbitSecuritySettings(this WebApplicationBuilder builder)
    {
        if (BuildTimeDocumentGeneration.IsActive)
            return builder;

        var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>();
        jwtSettings?.Validate();

        if (!builder.Environment.IsDevelopment())
        {
            var stripeSettings = builder.Configuration.GetSection(StripeSettings.SectionName).Get<StripeSettings>();
            stripeSettings?.ValidatePriceIds();

            var googlePlaySettings = builder.Configuration.GetSection(GooglePlaySettings.SectionName).Get<GooglePlaySettings>()
                ?? new GooglePlaySettings();
            googlePlaySettings.Validate();
        }

        if (!builder.Environment.IsProduction())
            return builder;

        var googleSettings = builder.Configuration.GetSection(GoogleSettings.SectionName).Get<GoogleSettings>();
        if (googleSettings?.AllowedRedirectUris is not { Length: > 0 }
            || googleSettings.AllowedRedirectUris.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Production requires Google:AllowedRedirectUris to contain valid redirect URIs.");

        var encryptionKey = builder.Configuration[$"{EncryptionSettings.SectionName}:Key"];
        if (string.IsNullOrWhiteSpace(encryptionKey) || encryptionKey.Contains("REPLACE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Production requires a configured Encryption:Key for protected-at-rest fields.");

        return builder;
    }

    public static WebApplicationBuilder AddOrbitDatabase(this WebApplicationBuilder builder)
    {
        var databaseSettings = DatabaseConnectionSettings.From(builder.Configuration);
        builder.Services.AddSingleton(databaseSettings);
        builder.Services.AddSingleton<SlowQueryCommandInterceptor>();
        builder.Services.AddSingleton<IAccountEventBus, InMemoryAccountEventBus>();
        builder.Services.AddScoped<AccountEventCollector>();
        builder.Services.AddScoped<IAccountEventCollector>(sp => sp.GetRequiredService<AccountEventCollector>());
        builder.Services.AddScoped<AccountEventTransactionInterceptor>();
        builder.Services.AddDbContext<OrbitDbContext>((serviceProvider, options) =>
            options
                .UseNpgsql(
                    OrbitConnectionStringFactory.ForRequestPath(builder.Configuration),
                    npgsql =>
                    {
                        npgsql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);
                        npgsql.CommandTimeout(databaseSettings.CommandTimeoutSeconds);
                    })
                .AddInterceptors(
                    serviceProvider.GetRequiredService<SlowQueryCommandInterceptor>(),
                    serviceProvider.GetRequiredService<AccountEventCollector>(),
                    serviceProvider.GetRequiredService<AccountEventTransactionInterceptor>()));

        builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
        builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
        builder.Services.AddScoped<EventTicketService>();
        builder.Services.AddScoped<IAccountResetRepository, AccountResetRepository>();
        builder.Services.AddScoped<IFoundingAchievementReader, FoundingAchievementReader>();
        builder.Services.AddScoped<IIdempotencyStore, IdempotencyStore>();
        builder.Services.AddScoped<IClosedMonthRecapStore, ClosedMonthRecapStore>();
        builder.Services.AddScoped<IAppConfigService, AppConfigService>();
        builder.Services.AddSingleton<Orbit.Application.Auth.Services.EmailChallengeService>();
        builder.Services.AddScoped<Orbit.Application.ApiKeys.Services.ApiKeyManagementAuthorization>();
        builder.Services.AddScoped<IAgentStepUpAuthorizationBridge>(sp =>
            sp.GetRequiredService<Orbit.Application.ApiKeys.Services.ApiKeyManagementAuthorization>());
        builder.Services.AddScoped<IUserDateService, UserDateService>();
        builder.Services.AddScoped(sp =>
        {
            var snapshots = new HabitScheduleSnapshotStore(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>());
            HabitScheduleSnapshotInvalidation.Attach(sp.GetRequiredService<OrbitDbContext>(), snapshots);
            return snapshots;
        });
        builder.Services.AddScoped<IUserStreakService, UserStreakService>();
        builder.Services.AddScoped<IGoalProgressReadSyncer, GoalProgressReadSyncer>();
        builder.Services.AddScoped<IGoalCompletionService, GoalCompletionService>();
        builder.Services.AddScoped<IPayGateService, PayGateService>();
        builder.Services.AddScoped<IFeatureFlagService, FeatureFlagService>();
        builder.Services.AddScoped<GamificationRepositories>(sp =>
            new GamificationRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.User>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.HabitLog>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Goal>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.UserAchievement>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Notification>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.XpAwardLog>>(),
                sp.GetRequiredService<IFoundingAchievementReader>()));
        builder.Services.AddScoped<IXpAwarder, Orbit.Application.Gamification.Services.XpAwarder>();
        builder.Services.AddScoped<IGamificationService, GamificationService>();
        builder.Services.AddScoped<Orbit.Application.Gamification.Services.IAchievementProgressService, Orbit.Application.Gamification.Services.AchievementProgressService>();
        builder.Services.AddScoped<Orbit.Application.Gamification.Backfill.XpAwardLogBackfillService>();
        builder.Services.AddScoped<Orbit.Application.Social.Services.SocialAccessGuard>();
        builder.Services.AddScoped<Orbit.Application.Social.Services.FriendGraphService>();
        builder.Services.AddScoped<Orbit.Application.Social.Services.SocialNotificationDispatcher>();
        builder.Services.AddScoped<Orbit.Application.Social.Services.IFriendFeedEventEmitter, Orbit.Application.Social.Services.FriendFeedEmitter>();
        builder.Services.AddScoped<IFriendFeedReader, FriendFeedReader>();
        builder.Services.AddScoped<ISocialGraphReader, SocialGraphReader>();
        builder.Services.AddScoped<IHabitLogReader, HabitLogReader>();
        builder.Services.AddScoped<IHabitScheduleLogReader, HabitScheduleLogReader>();
        builder.Services.AddScoped<IHabitSummaryLogReader, HabitSummaryLogReader>();
        builder.Services.AddScoped<IHabitSchedulePageLoader, HabitSchedulePageLoader>();
        builder.Services.AddScoped<Orbit.Application.Challenges.Services.IChallengeProgressService, Orbit.Application.Challenges.Services.ChallengeProgressService>();
        builder.Services.AddScoped<Orbit.Application.Challenges.Services.ChallengeProgressRepositories>(sp =>
            new Orbit.Application.Challenges.Services.ChallengeProgressRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Challenge>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.ChallengeParticipant>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.ChallengeParticipantHabit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.HabitLog>>()));
        builder.Services.AddScoped<Orbit.Application.Social.Commands.SendCheerRepositories>(sp =>
            new Orbit.Application.Social.Commands.SendCheerRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.User>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Cheer>>()));
        builder.Services.AddScoped<Orbit.Application.Accountability.Services.AccountabilityPairService>();
        builder.Services.AddScoped<Orbit.Application.Accountability.Commands.AccountabilityRepositories>(sp =>
            new Orbit.Application.Accountability.Commands.AccountabilityRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.User>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.AccountabilityPair>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.AccountabilityCheckIn>>()));
        builder.Services.AddScoped<Orbit.Application.Goals.Commands.GoalRepositories>(sp =>
            new Orbit.Application.Goals.Commands.GoalRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Goal>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.GoalProgressLog>>()));
        builder.Services.AddScoped<Orbit.Application.Habits.Commands.BulkCreateHabitsRepositories>(sp =>
            new Orbit.Application.Habits.Commands.BulkCreateHabitsRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.GoogleCalendarSyncSuggestion>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Tag>>()));
        builder.Services.AddScoped<IHabitSkipUndoWriter, HabitSkipUndoWriter>();
        builder.Services.AddScoped<Orbit.Application.Habits.Commands.SkipHabitRepositories>(sp =>
            new Orbit.Application.Habits.Commands.SkipHabitRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.HabitLog>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.HabitSkipUndo>>()));
        builder.Services.AddScoped<Orbit.Application.Calendar.Queries.GetCalendarEventsRepositories>(sp =>
            new Orbit.Application.Calendar.Queries.GetCalendarEventsRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.User>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.GoogleCalendarSyncSuggestion>>()));
        builder.Services.AddScoped<Orbit.Application.Profile.Commands.ApplyOnboardingRepositories>(sp =>
            new Orbit.Application.Profile.Commands.ApplyOnboardingRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.User>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Goal>>()));
        builder.Services.AddScoped<Orbit.Application.Challenges.Commands.CreateChallengeRepositories>(sp =>
            new Orbit.Application.Challenges.Commands.CreateChallengeRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Challenge>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.User>>()));
        builder.Services.AddScoped<Orbit.Application.Social.Queries.GetFriendProfileRepositories>(sp =>
            new Orbit.Application.Social.Queries.GetFriendProfileRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.User>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.UserAchievement>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.AccountabilityPair>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Challenge>>()));
        builder.Services.AddScoped<Orbit.Infrastructure.Services.UserStreakRepositories>(sp =>
            new Orbit.Infrastructure.Services.UserStreakRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.User>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.HabitLog>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.StreakFreeze>>()));
        builder.Services.AddScoped<Orbit.Application.Profile.Queries.ExportUserDataRepositories>(sp =>
            new Orbit.Application.Profile.Queries.ExportUserDataRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.User>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.HabitLog>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Goal>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.GoalProgressLog>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Tag>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.UserFact>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Notification>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.ChecklistTemplate>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.UserAchievement>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.StreakFreeze>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Referral>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.ApiKey>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Friendship>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Cheer>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.BlockedUser>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Report>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.FriendFeedEvent>>()));
        builder.Services.AddScoped<Orbit.Application.Social.Services.SocialInteractionServices>();
        builder.Services.AddScoped<Orbit.Application.Gamification.Services.GamificationNotifiers>();
        builder.Services.AddScoped<IGoogleTokenService, GoogleTokenService>();
        builder.Services.AddScoped<GoogleSignInFlow>();
        builder.Services.AddScoped<IGoogleAuthorizationCodeService, GoogleAuthorizationCodeService>();
        builder.Services.AddScoped<IGoogleIdTokenValidator, GoogleIdTokenValidator>();
        builder.Services.AddGoogleCalendarServices(GetDefaultHttpTimeout(builder));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<ITokenService, JwtTokenService>();
        builder.Services.AddScoped<IAuthSessionService, AuthSessionService>();

        return builder;
    }

    public static WebApplicationBuilder AddOrbitAuthentication(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<JwtSettings>(
            builder.Configuration.GetSection(JwtSettings.SectionName));

        JwtSettings jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>()
            ?? throw new InvalidOperationException("Configuration section 'Jwt' is missing.");
        if (string.IsNullOrWhiteSpace(jwtSettings.SecretKey)
            || string.IsNullOrWhiteSpace(jwtSettings.Issuer)
            || string.IsNullOrWhiteSpace(jwtSettings.Audience))
            throw new InvalidOperationException(
                "Configuration section 'Jwt' is incomplete; SecretKey, Issuer, and Audience are required.");

        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = "MultiScheme";
            options.DefaultChallengeScheme = "MultiScheme";
        })
        .AddJwtBearer("JwtBearer", options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtSettings.Issuer,
                ValidAudience = jwtSettings.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(jwtSettings.SecretKey)),
                ClockSkew = TimeSpan.Zero
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    var sessionClaim = context.Principal?.FindFirst("orbit_session_id")?.Value;
                    if (sessionClaim is null)
                        return;

                    var userClaim = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                    if (!Guid.TryParse(sessionClaim, out var sessionId)
                        || !Guid.TryParse(userClaim, out var userId)
                        || !await context.HttpContext.RequestServices
                            .GetRequiredService<IAuthSessionService>()
                            .IsSessionActiveAsync(sessionId, userId, context.HttpContext.RequestAborted))
                        context.Fail("Session is no longer active.");
                }
            };
        })
        .AddJwtBearer("EventTicket", options => ConfigureEventTicket(options, jwtSettings))
        .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>("ApiKey", null)
        .AddPolicyScheme("MultiScheme", "JWT or API Key", options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                if (HttpMethods.IsGet(context.Request.Method)
                    && context.Request.Path.Equals("/api/events", StringComparison.OrdinalIgnoreCase)
                    && context.Request.Query.ContainsKey("ticket"))
                    return "EventTicket";
                var auth = context.Request.Headers.Authorization.FirstOrDefault();
                if (auth?.StartsWith("Bearer orb_", StringComparison.OrdinalIgnoreCase) == true)
                    return "ApiKey";
                return "JwtBearer";
            };
        });

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy.Name, policy =>
                policy.Requirements.Add(new AdminRequirement()));
        builder.Services.AddScoped<IAuthorizationHandler, AdminAuthorizationHandler>();

        return builder;
    }

    private static void ConfigureEventTicket(JwtBearerOptions options, JwtSettings settings)
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = settings.Issuer,
            ValidAudience = EventTicketService.AudienceFor(settings),
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SecretKey)),
            ClockSkew = TimeSpan.Zero
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (HttpMethods.IsGet(context.Request.Method)
                    && context.Request.Path.Equals("/api/events", StringComparison.OrdinalIgnoreCase))
                    context.Token = context.Request.Query["ticket"].ToString();
                return Task.CompletedTask;
            },
            OnTokenValidated = async context =>
            {
                var sessionClaim = context.Principal?.FindFirst("orbit_session_id")?.Value;
                var userClaim = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (!Guid.TryParse(sessionClaim, out var sessionId)
                    || !Guid.TryParse(userClaim, out var userId)
                    || !await context.HttpContext.RequestServices
                        .GetRequiredService<IAuthSessionService>()
                        .IsSessionActiveAsync(sessionId, userId, context.HttpContext.RequestAborted))
                    context.Fail("Session is no longer active.");
            }
        };
    }

    public static WebApplicationBuilder AddOrbitAiServices(this WebApplicationBuilder builder)
    {
        AddAiPlatformServices(builder);
        AddAiChatTools(builder);
        AddHabitCommandDependencies(builder);
        AddCalendarCommandDependencies(builder);
        AddChatCommandDependencies(builder);

        return builder;
    }

    private static TimeSpan GetDefaultHttpTimeout(WebApplicationBuilder builder)
        => TimeSpan.FromSeconds(builder.Configuration.GetValue("HttpClients:DefaultTimeoutSeconds", 30));

    public static WebApplicationBuilder AddOrbitInfrastructure(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<EncryptionSettings>(
            builder.Configuration.GetSection(EncryptionSettings.SectionName));
        builder.Services.AddSingleton<IEncryptionService, EncryptionService>();

        builder.Services.Configure<FrontendSettings>(builder.Configuration.GetSection("Frontend"));

        var httpTimeout = GetDefaultHttpTimeout(builder);

        AddEmailAndSupabaseClients(builder, httpTimeout);

        builder.Services.Configure<GoogleSettings>(
            builder.Configuration.GetSection(GoogleSettings.SectionName));

        builder.Services.AddSingleton<OAuthAuthorizationStore>();
        builder.Services.AddHttpClient(GoogleTokenService.HttpClientName, client => client.Timeout = httpTimeout);
        builder.Services.AddHttpClient(GoogleAuthorizationCodeService.HttpClientName, client => client.Timeout = httpTimeout);

        AddStripeBilling(builder, httpTimeout);
        AddGooglePlayBilling(builder, httpTimeout);
        AddPushAndReferralServices(builder, httpTimeout);
        AddBackgroundServices(builder);

        InitializeFirebase(builder.Configuration);

        builder.Services.AddSingleton<IImageValidationService, ImageValidationService>();

        builder.Services.AddHttpClient<IGeoLocationService, GeoLocationService>()
            .ConfigureHttpClient(c => c.Timeout = httpTimeout);

        builder.Services.AddMemoryCache();
        AddOrbitDistributedCache(builder);

        builder.Services.AddValidatorsFromAssemblyContaining<CreateHabitCommandValidator>();

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IIdempotencyContext, HttpIdempotencyContext>();

        builder.Services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(Orbit.Application.Chat.Commands.ProcessUserChatCommand).Assembly);
            cfg.AddOpenBehavior(typeof(ConcurrencyRetryBehavior<,>));
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
            cfg.AddOpenBehavior(typeof(IdempotencyBehavior<,>));
        });

        AddCorsPolicies(builder);
        AddCookieAndKestrelLimits(builder);
        AddMcpToolServer(builder);
        AddApiPipeline(builder);

        return builder;
    }

    public static WebApplicationBuilder AddOrbitRateLimiting(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<IDistributedRateLimitService, DistributedRateLimitService>();
        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy(UploadReadRateLimitPolicy.Name, UploadReadRateLimitPolicy.GetPartition);
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        });

        return builder;
    }

    public static WebApplicationBuilder AddOrbitProductAnalytics(this WebApplicationBuilder builder)
    {
        var settings = builder.Configuration.GetSection(PostHogSettings.SectionName).Get<PostHogSettings>()
            ?? new PostHogSettings();

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            builder.Services.AddSingleton<IProductAnalytics, NoOpProductAnalytics>();
            return builder;
        }

        builder.Services.Configure<PostHogOptions>(options =>
        {
            options.ProjectToken = settings.ApiKey;
            options.HostUrl = new Uri(settings.HostUrl);
        });
        builder.AddPostHog();
        builder.Services.AddSingleton<IProductAnalytics, PostHogProductAnalytics>();

        return builder;
    }

    public static WebApplicationBuilder AddOrbitObservability(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<SentrySettings>(
            builder.Configuration.GetSection(SentrySettings.SectionName));

        var sentrySettings = builder.Configuration.GetSection(SentrySettings.SectionName).Get<SentrySettings>()
            ?? new SentrySettings();

        builder.WebHost.UseSentry(options =>
        {
            options.Dsn = sentrySettings.Dsn;
            options.Environment = sentrySettings.Environment;
            options.TracesSampleRate = sentrySettings.TracesSampleRate;
            options.EnableLogs = sentrySettings.EnableLogs;
            options.SendDefaultPii = false;
            options.AddExceptionFilterForType<FluentValidation.ValidationException>();
            options.AddExceptionFilterForType<OperationCanceledException>();
            options.SetBeforeSend(SentryEventScrubber.Scrub);
        });

        return builder;
    }
}
