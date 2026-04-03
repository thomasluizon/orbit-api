using System.Text;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Orbit.Api.Authentication;
using Orbit.Api.Mcp.Tools;
using Orbit.Api.Middleware;
using Orbit.Api.OAuth;
using Orbit.Api.OpenApi;
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
    public static WebApplicationBuilder AddOrbitDatabase(this WebApplicationBuilder builder)
    {
        builder.Services.AddDbContext<OrbitDbContext>(options =>
            options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

        builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
        builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
        builder.Services.AddScoped<IAccountResetRepository, AccountResetRepository>();
        builder.Services.AddScoped<IAppConfigService, AppConfigService>();
        builder.Services.AddScoped<IUserDateService, UserDateService>();
        builder.Services.AddScoped<IPayGateService, PayGateService>();
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
        builder.Services.AddScoped<ITokenService, JwtTokenService>();

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
        builder.Services.AddSingleton<AiCompletionClient>();
        builder.Services.AddScoped<IAiIntentService, AiIntentService>();
        builder.Services.AddScoped<IFactExtractionService, AiFactExtractionService>();
        builder.Services.AddScoped<ISummaryService, AiSummaryService>();
        builder.Services.AddScoped<IRetrospectiveService, AiRetrospectiveService>();
        builder.Services.AddScoped<IGoalReviewService, AiGoalReviewService>();

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
        builder.Services.AddScoped<IAiTool, UpdateGoalProgressTool>();
        builder.Services.AddScoped<IAiTool, LinkHabitsToGoalTool>();
        builder.Services.AddScoped<IAiTool, GoalReviewTool>();
        builder.Services.AddScoped<AiToolRegistry>();
        builder.Services.AddSingleton<ISystemPromptBuilder, SystemPromptBuilder>();

        // Handler Parameter Objects
        builder.Services.AddScoped<Orbit.Application.Habits.Commands.LogHabitRepositories>(sp =>
            new Orbit.Application.Habits.Commands.LogHabitRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.HabitLog>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Goal>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.User>>()));

        builder.Services.AddScoped<Orbit.Application.Habits.Commands.CreateHabitRepositories>(sp =>
            new Orbit.Application.Habits.Commands.CreateHabitRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Tag>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Goal>>()));

        builder.Services.AddScoped<Orbit.Application.Chat.Commands.ChatAiDependencies>(sp =>
            new Orbit.Application.Chat.Commands.ChatAiDependencies(
                sp.GetRequiredService<IAiIntentService>(),
                sp.GetRequiredService<AiToolRegistry>(),
                sp.GetRequiredService<ISystemPromptBuilder>()));
        builder.Services.AddScoped<Orbit.Application.Chat.Commands.ChatDataDependencies>(sp =>
            new Orbit.Application.Chat.Commands.ChatDataDependencies(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.User>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.UserFact>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Tag>>()));

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

        // CORS
        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? ["http://localhost:3000"];
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.WithOrigins(allowedOrigins)
                      .WithHeaders("Authorization", "Content-Type", "Mcp-Session-Id")
                      .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS")
                      .AllowCredentials();
            });
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
        builder.Services.AddProblemDetails();

        // OpenAPI + Scalar
        builder.Services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
        });

        return builder;
    }

    public static WebApplicationBuilder AddOrbitRateLimiting(this WebApplicationBuilder builder)
    {
        // NOTE: IMemoryCache-backed rate limiting is not shared across replicas.
        // For multi-replica deployments, replace with a distributed store (e.g., Redis).
        // Disabled in non-Production to avoid breaking integration tests.
        if (builder.Environment.IsProduction())
        {
            builder.Services.AddRateLimiter(options =>
            {
                options.AddFixedWindowLimiter("auth", limiterOptions =>
                {
                    limiterOptions.Window = TimeSpan.FromMinutes(1);
                    limiterOptions.PermitLimit = 5;
                    limiterOptions.QueueLimit = 0;
                    limiterOptions.AutoReplenishment = true;
                });
                options.AddSlidingWindowLimiter("chat", limiterOptions =>
                {
                    limiterOptions.Window = TimeSpan.FromMinutes(1);
                    limiterOptions.SegmentsPerWindow = 4;
                    limiterOptions.PermitLimit = 20;
                    limiterOptions.QueueLimit = 0;
                    limiterOptions.AutoReplenishment = true;
                });
                options.AddFixedWindowLimiter("support", limiterOptions =>
                {
                    limiterOptions.Window = TimeSpan.FromHours(1);
                    limiterOptions.PermitLimit = 3;
                    limiterOptions.QueueLimit = 0;
                    limiterOptions.AutoReplenishment = true;
                });
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            });
        }

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
