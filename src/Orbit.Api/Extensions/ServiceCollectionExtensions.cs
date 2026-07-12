using System.Text;
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
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Services;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Services.Calendar;

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
        builder.Services.AddDbContext<OrbitDbContext>((serviceProvider, options) =>
            options
                .UseNpgsql(
                    OrbitConnectionStringFactory.ForRequestPath(builder.Configuration),
                    npgsql =>
                    {
                        npgsql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);
                        npgsql.CommandTimeout(databaseSettings.CommandTimeoutSeconds);
                    })
                .AddInterceptors(serviceProvider.GetRequiredService<SlowQueryCommandInterceptor>()));

        builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
        builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
        builder.Services.AddScoped<IAccountResetRepository, AccountResetRepository>();
        builder.Services.AddScoped<IIdempotencyStore, IdempotencyStore>();
        builder.Services.AddScoped<IAppConfigService, AppConfigService>();
        builder.Services.AddScoped<IUserDateService, UserDateService>();
        builder.Services.AddScoped<IUserStreakService, UserStreakService>();
        builder.Services.AddScoped<IStreakGoalReadSyncer, StreakGoalReadSyncer>();
        builder.Services.AddScoped<IPayGateService, PayGateService>();
        builder.Services.AddScoped<IFeatureFlagService, FeatureFlagService>();
        builder.Services.AddScoped<GamificationRepositories>(sp =>
            new GamificationRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.User>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.HabitLog>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Goal>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.UserAchievement>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Notification>>()));
        builder.Services.AddScoped<IXpAwarder, Orbit.Application.Gamification.Services.XpAwarder>();
        builder.Services.AddScoped<IGamificationService, GamificationService>();
        builder.Services.AddScoped<Orbit.Application.Gamification.Backfill.XpAwardLogBackfillService>();
        builder.Services.AddScoped<Orbit.Application.Social.Services.SocialAccessGuard>();
        builder.Services.AddScoped<Orbit.Application.Social.Services.FriendGraphService>();
        builder.Services.AddScoped<Orbit.Application.Social.Services.SocialNotificationDispatcher>();
        builder.Services.AddScoped<Orbit.Application.Social.Services.IFriendFeedEventEmitter, Orbit.Application.Social.Services.FriendFeedEmitter>();
        builder.Services.AddScoped<IFriendFeedReader, FriendFeedReader>();
        builder.Services.AddScoped<ISocialGraphReader, SocialGraphReader>();
        builder.Services.AddScoped<IHabitLogReader, HabitLogReader>();
        builder.Services.AddScoped<Orbit.Application.Challenges.Services.IChallengeProgressService, Orbit.Application.Challenges.Services.ChallengeProgressService>();
        builder.Services.AddScoped<Orbit.Application.Challenges.Services.ChallengeProgressRepositories>(sp =>
            new Orbit.Application.Challenges.Services.ChallengeProgressRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Challenge>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.ChallengeParticipant>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.ChallengeParticipantHabit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.HabitLog>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.User>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.UserAchievement>>()));
        builder.Services.AddScoped<Orbit.Application.Social.Commands.SendCheerRepositories>(sp =>
            new Orbit.Application.Social.Commands.SendCheerRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.User>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Cheer>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.UserAchievement>>()));
        builder.Services.AddScoped<Orbit.Application.Accountability.Services.AccountabilityPairService>();
        builder.Services.AddScoped<Orbit.Application.Accountability.Commands.AccountabilityRepositories>(sp =>
            new Orbit.Application.Accountability.Commands.AccountabilityRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.User>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.AccountabilityPair>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.AccountabilityCheckIn>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.UserAchievement>>()));
        builder.Services.AddScoped<IGoogleTokenService, GoogleTokenService>();
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

        var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>()!;

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
        })
        .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>("ApiKey", null)
        .AddPolicyScheme("MultiScheme", "JWT or API Key", options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                var auth = context.Request.Headers.Authorization.FirstOrDefault();
                if (auth?.StartsWith("Bearer orb_", StringComparison.OrdinalIgnoreCase) == true)
                    return "ApiKey";
                return "JwtBearer";
            };
        });

        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(AdminPolicy.Name, policy =>
                policy.Requirements.Add(new AdminRequirement()));
        });
        builder.Services.AddScoped<IAuthorizationHandler, AdminAuthorizationHandler>();

        return builder;
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
