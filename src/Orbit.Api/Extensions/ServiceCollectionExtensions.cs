using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Orbit.Api.Authentication;
using Orbit.Api.Mcp.Tools;
using Orbit.Api.Middleware;
using Orbit.Api.OAuth;
using Orbit.Api.OpenApi;
using Orbit.Api.RateLimiting;
using Orbit.Application.Behaviors;
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
        // JWT secret strength check applies in every environment -- a weak key in dev/staging
        // becomes a weak key in prod the moment someone copy-pastes appsettings.
        var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>();
        jwtSettings?.Validate();

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
                    // Retry transient PostgreSQL disconnects (Supabase pooler can drop idle
                    // connections). Without this, a single transient blip surfaces as an
                    // uncaught NpgsqlException at the request boundary.
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
        builder.Services.AddScoped<IAgentStepUpService, AgentStepUpService>();
        builder.Services.AddScoped<IAgentPolicyEvaluator, AgentPolicyEvaluator>();
        builder.Services.AddScoped<IAgentAuditService, AgentAuditService>();
        builder.Services.AddScoped<IAgentTargetOwnershipService, AgentTargetOwnershipService>();
        builder.Services.AddScoped<IAgentOperationExecutor, AgentOperationExecutor>();

        // AI Tool Registration
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
        builder.Services.AddScoped<IAiTool, ActivateStreakFreezeTool>();
        builder.Services.AddScoped<IAiTool, GetReferralOverviewTool>();
        builder.Services.AddScoped<IAiTool, GetSubscriptionOverviewTool>();
        builder.Services.AddScoped<IAiTool, ManageSubscriptionTool>();
        builder.Services.AddScoped<IAiTool, GetApiKeysTool>();
        builder.Services.AddScoped<IAiTool, ManageApiKeysTool>();
        builder.Services.AddScoped<IAiTool, SendSupportRequestTool>();
        builder.Services.AddScoped<IAiTool, ManageAccountTool>();
        builder.Services.AddScoped<AiToolRegistry>();
        builder.Services.AddSingleton<ISystemPromptBuilder, SystemPromptBuilder>();

        // Handler Parameter Objects
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
                sp.GetRequiredService<IAgentOperationExecutor>()));

        return builder;
    }

    public static WebApplicationBuilder AddOrbitInfrastructure(this WebApplicationBuilder builder)
    {
        // Encryption
        builder.Services.Configure<EncryptionSettings>(
            builder.Configuration.GetSection(EncryptionSettings.SectionName));
        builder.Services.AddSingleton<IEncryptionService, EncryptionService>();

        // Frontend Settings
        builder.Services.Configure<FrontendSettings>(builder.Configuration.GetSection("Frontend"));

        // HTTP Client Timeout
        var httpTimeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("HttpClients:DefaultTimeoutSeconds", 30));

        // Supabase (OAuth token validation)
        builder.Services.AddHttpClient("Supabase", client =>
        {
            client.BaseAddress = new Uri(builder.Configuration["Supabase:Url"]!);
            client.DefaultRequestHeaders.Add("apikey", builder.Configuration["Supabase:AnonKey"]!);
            client.Timeout = httpTimeout;
        });

        // Resend (Email)
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

        // Google
        builder.Services.Configure<GoogleSettings>(
            builder.Configuration.GetSection(GoogleSettings.SectionName));

        // OAuth
        builder.Services.AddSingleton<OAuthAuthorizationStore>();

        // Stripe
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
        // IBillingService wraps every Stripe SDK call used by checkout, portal, plans,
        // and billing-details so the Application layer has no Stripe imports.
        builder.Services.AddScoped<Orbit.Application.Common.IBillingService, Orbit.Infrastructure.Services.StripeBillingService>();

        // Push Notifications (VAPID + FCM)
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

        // Background Services
        builder.Services.AddHostedService<ReminderSchedulerService>();
        builder.Services.AddHostedService<GoalDeadlineNotificationService>();
        builder.Services.AddHostedService<SlipAlertSchedulerService>();
        builder.Services.AddHostedService<AccountDeletionService>();
        builder.Services.AddHostedService<HabitDueDateAdvancementService>();
        builder.Services.AddHostedService<DataEncryptionMigrationService>();
        builder.Services.AddHostedService<SyncCleanupService>();
        builder.Services.AddHostedService<CalendarAutoSyncService>();
        builder.Services.AddScoped<ISlipAlertMessageService, AiSlipAlertMessageService>();

        // Health Checks
        builder.Services.AddHealthChecks()
            .AddCheck<BackgroundServiceHealthCheck>("background-services");

        // Firebase Admin SDK for FCM
        InitializeFirebase(builder.Configuration);

        // Image Validation
        builder.Services.AddSingleton<IImageValidationService, ImageValidationService>();

        // Geo Location
        builder.Services.AddHttpClient<IGeoLocationService, GeoLocationService>()
            .ConfigureHttpClient(c => c.Timeout = httpTimeout);

        // In-Memory Cache
        builder.Services.AddMemoryCache();

        // Validation
        builder.Services.AddValidatorsFromAssemblyContaining<CreateHabitCommandValidator>();

        // MediatR
        builder.Services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(Orbit.Application.Chat.Commands.ProcessUserChatCommand).Assembly);
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });

        // CORS -- two policies:
        //  1. Default credentialed policy for first-party Orbit web/mobile origins (cookie auth).
        //  2. Separate non-credentialed policy for third-party MCP origins (claude.ai/claude.com)
        //     which authenticate via Bearer API key, not cookies. Allowing credentials with
        //     third-party origins would let a malicious page issue cookie-bearing requests on
        //     behalf of an authenticated user.
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
                    // No AllowCredentials() -- third-party origins use Bearer token auth.
                });
            }
        });

        // Cookie Security Policy
        builder.Services.Configure<CookiePolicyOptions>(options =>
        {
            options.HttpOnly = Microsoft.AspNetCore.CookiePolicy.HttpOnlyPolicy.Always;
            options.Secure = CookieSecurePolicy.Always;
            options.MinimumSameSitePolicy = SameSiteMode.Strict;
        });

        // Request Size Limit
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10MB global default
        });

        // MCP Server
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
            .WithTools<CalendarTools>();

        // Controllers
        builder.Services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            });

        // Exception Handling
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

        // OpenAPI + Scalar
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
