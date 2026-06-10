using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Orbit.Api.Authentication;
using Orbit.Api.Mcp.Tools;
using Orbit.Api.Middleware;
using Orbit.Api.OAuth;
using Orbit.Api.OpenApi;
using Orbit.Api.RateLimiting;
using Orbit.Application.Behaviors;
using Orbit.Application.Chat.FeatureExplanations;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Services;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Scalar.AspNetCore;

namespace Orbit.Api.Extensions;

public static class ServiceCollectionExtensions
{
    public static WebApplicationBuilder ValidateOrbitSecuritySettings(this WebApplicationBuilder builder)
    {
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
        builder.Services.AddDbContext<OrbitDbContext>(options =>
            options.UseNpgsql(
                builder.Configuration.GetConnectionString("DefaultConnection"),
                npgsql =>
                {
                    npgsql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);
                    npgsql.CommandTimeout(30);
                }));

        builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
        builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
        builder.Services.AddScoped<IAccountResetRepository, AccountResetRepository>();
        builder.Services.AddScoped<IAppConfigService, AppConfigService>();
        builder.Services.AddScoped<IUserDateService, UserDateService>();
        builder.Services.AddScoped<IUserStreakService, UserStreakService>();
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
        builder.Services.AddScoped<IGamificationService, GamificationService>();
        builder.Services.AddScoped<IGoogleTokenService, GoogleTokenService>();
        builder.Services.AddScoped<Orbit.Application.Calendar.Services.ICalendarEventFetcher, Orbit.Infrastructure.Services.GoogleCalendarEventFetcher>();
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

        builder.Services.AddAuthorization();

        return builder;
    }

    public static WebApplicationBuilder AddOrbitAiServices(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<AiSettings>(
            builder.Configuration.GetSection(AiSettings.SectionName));
        builder.Services.Configure<AgentPlatformSettings>(
            builder.Configuration.GetSection(AgentPlatformSettings.SectionName));
        builder.Services.AddSingleton<AiCompletionClient>();
        builder.Services.AddScoped<IAiIntentService, AiIntentService>();
        builder.Services.AddScoped<IFactExtractionService, AiFactExtractionService>();
        builder.Services.AddScoped<ISummaryService, AiSummaryService>();
        builder.Services.AddScoped<IRetrospectiveService, AiRetrospectiveService>();
        builder.Services.AddScoped<IGoalReviewService, AiGoalReviewService>();
        builder.Services.AddScoped<IAgentCatalogService, AgentCatalogService>();
        builder.Services.AddScoped<IPendingAgentOperationStore, PendingAgentOperationStore>();
        builder.Services.AddScoped<IPendingClarificationStore, PendingClarificationStore>();
        builder.Services.AddScoped<IAgentStepUpService, AgentStepUpService>();
        builder.Services.AddScoped<IAgentPolicyEvaluator, AgentPolicyEvaluator>();
        builder.Services.AddScoped<IAgentAuditService, AgentAuditService>();
        builder.Services.AddScoped<IAgentTargetOwnershipService, AgentTargetOwnershipService>();
        builder.Services.AddScoped<IAgentOperationExecutor, AgentOperationExecutor>();
        builder.Services.AddScoped<Orbit.Api.Mcp.McpExecutorBridge>();

        builder.Services.AddScoped<IAiTool, LogHabitTool>();
        builder.Services.AddScoped<IAiTool, SkipHabitTool>();
        builder.Services.AddScoped<IAiTool, CreateHabitTool>();
        builder.Services.AddScoped<IAiTool, UpdateHabitTool>();
        builder.Services.AddScoped<IAiTool, DeleteHabitTool>();
        builder.Services.AddScoped<IAiTool, CreateSubHabitTool>();
        builder.Services.AddScoped<IAiTool, AssignTagsTool>();
        builder.Services.AddScoped<IAiTool, SuggestBreakdownTool>();
        builder.Services.AddScoped<IAiTool, DuplicateHabitTool>();
        builder.Services.AddScoped<IAiTool, MoveHabitTool>();
        builder.Services.AddScoped<IAiTool, BulkUpdateHabitEmojisTool>();
        builder.Services.AddScoped<IAiTool, BulkLogHabitsTool>();
        builder.Services.AddScoped<IAiTool, BulkSkipHabitsTool>();
        builder.Services.AddScoped<IAiTool, QueryHabitsTool>();
        builder.Services.AddScoped<IAiTool, CreateGoalTool>();
        builder.Services.AddScoped<IAiTool, QueryGoalsTool>();
        builder.Services.AddScoped<IAiTool, UpdateGoalTool>();
        builder.Services.AddScoped<IAiTool, DeleteGoalTool>();
        builder.Services.AddScoped<IAiTool, UpdateGoalStatusTool>();
        builder.Services.AddScoped<IAiTool, UpdateGoalProgressTool>();
        builder.Services.AddScoped<IAiTool, LinkHabitsToGoalTool>();
        builder.Services.AddScoped<IAiTool, GoalReviewTool>();
        builder.Services.AddScoped<IAiTool, GetProfileTool>();
        builder.Services.AddScoped<IAiTool, UpdateProfilePreferencesTool>();
        builder.Services.AddScoped<IAiTool, SetColorSchemeTool>();
        builder.Services.AddScoped<IAiTool, SetAiMemoryTool>();
        builder.Services.AddScoped<IAiTool, SetAiSummaryTool>();
        builder.Services.AddScoped<IAiTool, GetNotificationsTool>();
        builder.Services.AddScoped<IAiTool, UpdateNotificationsTool>();
        builder.Services.AddScoped<IAiTool, DeleteNotificationsTool>();
        builder.Services.AddScoped<IAiTool, GetCalendarOverviewTool>();
        builder.Services.AddScoped<IAiTool, ManageCalendarSyncTool>();
        builder.Services.AddScoped<IAiTool, GetChecklistTemplatesTool>();
        builder.Services.AddScoped<IAiTool, CreateChecklistTemplateTool>();
        builder.Services.AddScoped<IAiTool, DeleteChecklistTemplateTool>();
        builder.Services.AddScoped<IAiTool, GetUserFactsTool>();
        builder.Services.AddScoped<IAiTool, DeleteUserFactsTool>();
        builder.Services.AddScoped<IAiTool, GetGamificationOverviewTool>();
        builder.Services.AddScoped<IAiTool, GetReferralOverviewTool>();
        builder.Services.AddScoped<IAiTool, GetSubscriptionOverviewTool>();
        builder.Services.AddScoped<IAiTool, ManageSubscriptionTool>();
        builder.Services.AddScoped<IAiTool, GetApiKeysTool>();
        builder.Services.AddScoped<IAiTool, ManageApiKeysTool>();
        builder.Services.AddScoped<IAiTool, SendSupportRequestTool>();
        builder.Services.AddScoped<IAiTool, ManageAccountTool>();
        builder.Services.AddScoped<IAiTool, UpdateChecklistTool>();
        builder.Services.AddScoped<IAiTool, ReorderHabitsTool>();
        builder.Services.AddScoped<IAiTool, MoveHabitParentTool>();
        builder.Services.AddScoped<IAiTool, LinkGoalsToHabitTool>();
        builder.Services.AddScoped<IAiTool, BulkCreateHabitsTool>();
        builder.Services.AddScoped<IAiTool, BulkDeleteHabitsTool>();
        builder.Services.AddScoped<IAiTool, GetDailySummaryTool>();
        builder.Services.AddScoped<IAiTool, GetRetrospectiveTool>();
        builder.Services.AddScoped<IAiTool, GetHabitMetricsTool>();
        builder.Services.AddScoped<IAiTool, DescribeFeatureTool>();
        builder.Services.AddScoped<IAiTool, ListTagsTool>();
        builder.Services.AddScoped<IAiTool, CreateTagTool>();
        builder.Services.AddScoped<IAiTool, UpdateTagTool>();
        builder.Services.AddScoped<IAiTool, DeleteTagTool>();
        builder.Services.AddScoped<IAiTool, ReorderGoalsTool>();
        builder.Services.AddScoped<IAiTool, GetReferralCodeTool>();
        builder.Services.AddScoped<AiToolRegistry>();
        builder.Services.AddSingleton<ISystemPromptBuilder, SystemPromptBuilder>();
        builder.Services.AddSingleton<IFeatureExplanationService, FeatureExplanationService>();

        builder.Services.AddScoped<Orbit.Application.Habits.Commands.LogHabitRepositories>(sp =>
            new Orbit.Application.Habits.Commands.LogHabitRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.HabitLog>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Goal>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.User>>()));
        builder.Services.AddScoped<Orbit.Application.Habits.Commands.LogHabitServices>(sp =>
            new Orbit.Application.Habits.Commands.LogHabitServices(
                sp.GetRequiredService<IUserDateService>(),
                sp.GetRequiredService<IUserStreakService>(),
                sp.GetRequiredService<IGamificationService>(),
                sp.GetRequiredService<MediatR.IMediator>()));
        builder.Services.AddScoped<Orbit.Application.Habits.Commands.BulkLogServices>(sp =>
            new Orbit.Application.Habits.Commands.BulkLogServices(
                sp.GetRequiredService<IUserDateService>(),
                sp.GetRequiredService<IUserStreakService>(),
                sp.GetRequiredService<IGamificationService>()));

        builder.Services.AddScoped<Orbit.Application.Habits.Commands.CreateHabitRepositories>(sp =>
            new Orbit.Application.Habits.Commands.CreateHabitRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Tag>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Goal>>()));

        builder.Services.AddScoped<Orbit.Application.Calendar.Commands.CalendarAutoSyncDependencies>(sp =>
            new Orbit.Application.Calendar.Commands.CalendarAutoSyncDependencies(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.User>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.GoogleCalendarSyncSuggestion>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Notification>>(),
                sp.GetRequiredService<IGoogleTokenService>(),
                sp.GetRequiredService<Orbit.Application.Calendar.Services.ICalendarEventFetcher>(),
                sp.GetRequiredService<IUnitOfWork>()));

        builder.Services.AddScoped<Orbit.Application.Chat.Commands.ChatAiDependencies>(sp =>
            new Orbit.Application.Chat.Commands.ChatAiDependencies(
                sp.GetRequiredService<IAiIntentService>(),
                sp.GetRequiredService<AiToolRegistry>(),
                sp.GetRequiredService<ISystemPromptBuilder>(),
                sp.GetRequiredService<IAgentCatalogService>()));
        builder.Services.AddScoped<Orbit.Application.Chat.Commands.ChatDataDependencies>(sp =>
            new Orbit.Application.Chat.Commands.ChatDataDependencies(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Goal>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.User>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.UserFact>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Tag>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.ChecklistTemplate>>(),
                sp.GetRequiredService<IFeatureFlagService>()));
        builder.Services.AddScoped<Orbit.Application.Chat.Commands.ChatExecutionDependencies>(sp =>
            new Orbit.Application.Chat.Commands.ChatExecutionDependencies(
                sp.GetRequiredService<IUserDateService>(),
                sp.GetRequiredService<IUserStreakService>(),
                sp.GetRequiredService<IPayGateService>(),
                sp.GetRequiredService<IUnitOfWork>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IAgentOperationExecutor>(),
                sp.GetRequiredService<IPendingClarificationStore>()));

        return builder;
    }

    public static WebApplicationBuilder AddOrbitInfrastructure(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<EncryptionSettings>(
            builder.Configuration.GetSection(EncryptionSettings.SectionName));
        builder.Services.AddSingleton<IEncryptionService, EncryptionService>();

        builder.Services.Configure<FrontendSettings>(builder.Configuration.GetSection("Frontend"));

        var httpTimeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("HttpClients:DefaultTimeoutSeconds", 30));

        builder.Services.AddHttpClient("Supabase", client =>
        {
            client.BaseAddress = new Uri(builder.Configuration["Supabase:Url"]!);
            client.DefaultRequestHeaders.Add("apikey", builder.Configuration["Supabase:AnonKey"]!);
            client.Timeout = httpTimeout;
        });

        builder.Services.Configure<ResendSettings>(
            builder.Configuration.GetSection(ResendSettings.SectionName));

#pragma warning disable S1075 // Resend API base URL is a stable, well-known endpoint
        builder.Services.AddHttpClient("Resend", client =>
        {
            client.BaseAddress = new Uri("https://api.resend.com");
#pragma warning restore S1075
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {builder.Configuration["Resend:ApiKey"]}");
            client.Timeout = httpTimeout;
        });

        builder.Services.AddScoped<IEmailService, ResendEmailService>();

        builder.Services.Configure<GoogleSettings>(
            builder.Configuration.GetSection(GoogleSettings.SectionName));

        builder.Services.AddSingleton<OAuthAuthorizationStore>();

        builder.Services.Configure<StripeSettings>(
            builder.Configuration.GetSection(StripeSettings.SectionName));
        var stripeKey = builder.Configuration.GetSection(StripeSettings.SectionName).Get<StripeSettings>()?.SecretKey;
        if (!string.IsNullOrEmpty(stripeKey))
        {
            Stripe.StripeConfiguration.ApiKey = stripeKey;
        }

        builder.Services.AddSingleton<Stripe.CustomerService>();
        builder.Services.AddSingleton<Stripe.Checkout.SessionService>();
        builder.Services.AddSingleton<Stripe.BillingPortal.SessionService>();
        builder.Services.AddSingleton<Stripe.SubscriptionService>();
        builder.Services.AddSingleton<Stripe.InvoiceService>();
        builder.Services.AddSingleton<Stripe.PriceService>();
        builder.Services.AddSingleton<Stripe.CouponService>();
        builder.Services.AddScoped<Orbit.Application.Common.IBillingService, Orbit.Infrastructure.Services.StripeBillingService>();
        builder.Services.AddScoped<Orbit.Application.Subscriptions.Services.IPriceResolver, Orbit.Application.Subscriptions.Services.PriceResolver>();

        builder.Services.Configure<GooglePlaySettings>(
            builder.Configuration.GetSection(GooglePlaySettings.SectionName));
        builder.Services.AddSingleton(sp =>
        {
            var googlePlaySettings = sp.GetRequiredService<IOptions<GooglePlaySettings>>().Value;
            var credential = Google.Apis.Auth.OAuth2.CredentialFactory
                .FromJson<Google.Apis.Auth.OAuth2.ServiceAccountCredential>(googlePlaySettings.ServiceAccountJson)
                .ToGoogleCredential()
                .CreateScoped(Google.Apis.AndroidPublisher.v3.AndroidPublisherService.Scope.Androidpublisher);
            return new Google.Apis.AndroidPublisher.v3.AndroidPublisherService(
                new Google.Apis.Services.BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "Orbit",
                });
        });
        builder.Services.AddScoped<Orbit.Application.Common.IPlayBillingService, Orbit.Infrastructure.Services.GooglePlayBillingService>();
        builder.Services.AddSingleton<Orbit.Application.Common.IPlayPushTokenValidator, Orbit.Infrastructure.Services.GooglePlayPushTokenValidator>();
        builder.Services.AddScoped<Orbit.Application.Subscriptions.Services.IPlayReferralCouponConsumer, Orbit.Application.Subscriptions.Services.PlayReferralCouponConsumer>();

        builder.Services.Configure<VapidSettings>(
            builder.Configuration.GetSection(VapidSettings.SectionName));
        builder.Services.AddHttpClient<IPushNotificationService, PushNotificationService>()
            .ConfigureHttpClient(c => c.Timeout = httpTimeout);
        builder.Services.AddScoped<IReferralRewardService, StripeCouponRewardService>();
        builder.Services.AddScoped<Orbit.Application.Referrals.Commands.ReferralRepositories>(sp =>
            new Orbit.Application.Referrals.Commands.ReferralRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.User>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Referral>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.HabitLog>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Notification>>()));

        builder.Services.AddHostedService<ReminderSchedulerService>();
        builder.Services.AddHostedService<GoalDeadlineNotificationService>();
        builder.Services.AddHostedService<SlipAlertSchedulerService>();
        builder.Services.AddHostedService<AccountDeletionService>();
        builder.Services.AddHostedService<HabitDueDateAdvancementService>();
        builder.Services.AddHostedService<StreakFreezeAutoActivationService>();
        builder.Services.AddHostedService<DataEncryptionMigrationService>();
        builder.Services.AddHostedService<SyncCleanupService>();
        builder.Services.AddHostedService<PlayNotificationCleanupService>();
        builder.Services.AddHostedService<CalendarAutoSyncService>();
        builder.Services.AddScoped<ISlipAlertMessageService, AiSlipAlertMessageService>();

        builder.Services.AddHealthChecks()
            .AddCheck<BackgroundServiceHealthCheck>("background-services");

        InitializeFirebase(builder.Configuration);

        builder.Services.AddSingleton<IImageValidationService, ImageValidationService>();

        builder.Services.AddHttpClient<IGeoLocationService, GeoLocationService>()
            .ConfigureHttpClient(c => c.Timeout = httpTimeout);

        builder.Services.AddMemoryCache();

        builder.Services.AddValidatorsFromAssemblyContaining<CreateHabitCommandValidator>();

        builder.Services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(Orbit.Application.Chat.Commands.ProcessUserChatCommand).Assembly);
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });

        var firstPartyOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? ["http://localhost:3000"];
        var thirdPartyOrigins = builder.Configuration.GetSection("Cors:ThirdPartyOrigins").Get<string[]>()
            ?? [];
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.WithOrigins(firstPartyOrigins)
                      .WithHeaders("Authorization", "Content-Type", "Mcp-Session-Id")
                      .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS")
                      .AllowCredentials();
            });
            if (thirdPartyOrigins.Length > 0)
            {
                options.AddPolicy("ThirdParty", policy =>
                {
                    policy.WithOrigins(thirdPartyOrigins)
                          .WithHeaders("Authorization", "Content-Type", "Mcp-Session-Id")
                          .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS");
                });
            }
        });

        builder.Services.Configure<CookiePolicyOptions>(options =>
        {
            options.HttpOnly = Microsoft.AspNetCore.CookiePolicy.HttpOnlyPolicy.Always;
            options.Secure = CookieSecurePolicy.Always;
            options.MinimumSameSitePolicy = SameSiteMode.Strict;
        });

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 10 * 1024 * 1024;
        });

        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<AgentTools>()
            .WithTools<HabitTools>()
            .WithTools<TagTools>()
            .WithTools<GoalTools>()
            .WithTools<ProfileTools>()
            .WithTools<GamificationTools>()
            .WithTools<NotificationTools>()
            .WithTools<SubscriptionTools>()
            .WithTools<UserFactTools>()
            .WithTools<CalendarTools>()
            .WithTools<AccountTools>()
            .WithTools<ApiKeyTools>()
            .WithTools<SupportTools>()
            .WithTools<ChecklistTemplateTools>()
            .WithTools<FeatureTools>();

        builder.Services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            });

        builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
        builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();
        builder.Services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = ctx =>
            {
                ctx.ProblemDetails.Extensions.Remove("exception");
                ctx.ProblemDetails.Detail = null;
            };
        });

        builder.Services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
        });

        return builder;
    }

    public static WebApplicationBuilder AddOrbitRateLimiting(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<IDistributedRateLimitService, DistributedRateLimitService>();

        return builder;
    }

    private static void InitializeFirebase(ConfigurationManager configuration)
    {
        var firebaseCredJson = configuration["Firebase:CredentialsJson"];
        if (!string.IsNullOrEmpty(firebaseCredJson))
        {
            FirebaseAdmin.FirebaseApp.Create(new FirebaseAdmin.AppOptions
            {
                Credential = Google.Apis.Auth.OAuth2.CredentialFactory
                    .FromJson<Google.Apis.Auth.OAuth2.ServiceAccountCredential>(firebaseCredJson)
                    .ToGoogleCredential()
            });
            return;
        }

        var firebaseCredPath = configuration["Firebase:CredentialsPath"];
        if (!string.IsNullOrEmpty(firebaseCredPath) && File.Exists(firebaseCredPath))
        {
            FirebaseAdmin.FirebaseApp.Create(new FirebaseAdmin.AppOptions
            {
                Credential = Google.Apis.Auth.OAuth2.CredentialFactory
                    .FromFile<Google.Apis.Auth.OAuth2.ServiceAccountCredential>(firebaseCredPath)
                    .ToGoogleCredential()
            });
        }
    }
}
