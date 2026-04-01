using System.Text;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Authentication;
using Orbit.Api.Authentication;
using Orbit.Api.Mcp.Tools;
using Orbit.Api.OAuth;
using Orbit.Api.Middleware;
using Orbit.Api.OpenApi;
using Orbit.Application.Behaviors;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Gamification.Services;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// --- Encryption ---
builder.Services.Configure<EncryptionSettings>(
    builder.Configuration.GetSection(EncryptionSettings.SectionName));
builder.Services.AddSingleton<IEncryptionService, EncryptionService>();

// --- Database ---
builder.Services.AddDbContext<OrbitDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// --- Repositories & UoW ---
builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<IAppConfigService, AppConfigService>();
builder.Services.AddScoped<IUserDateService, UserDateService>();
builder.Services.AddScoped<IPayGateService, Orbit.Application.Common.PayGateService>();
builder.Services.AddScoped<IGamificationService, GamificationService>();
builder.Services.AddScoped<Orbit.Domain.Interfaces.IGoogleTokenService, GoogleTokenService>();

// --- Token Service ---
builder.Services.AddScoped<ITokenService, JwtTokenService>();

// --- Supabase (OAuth token validation) ---
builder.Services.AddHttpClient("Supabase", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Supabase:Url"]!);
    client.DefaultRequestHeaders.Add("apikey", builder.Configuration["Supabase:AnonKey"]!);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// --- Resend (Email) ---
builder.Services.Configure<ResendSettings>(
    builder.Configuration.GetSection(ResendSettings.SectionName));

builder.Services.AddHttpClient("Resend", client =>
{
    client.BaseAddress = new Uri("https://api.resend.com");
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {builder.Configuration["Resend:ApiKey"]}");
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddScoped<IEmailService, ResendEmailService>();

// --- Google ---
builder.Services.Configure<GoogleSettings>(
    builder.Configuration.GetSection(GoogleSettings.SectionName));

// --- OAuth ---
builder.Services.AddSingleton<OAuthAuthorizationStore>();

// --- Stripe ---
builder.Services.Configure<StripeSettings>(
    builder.Configuration.GetSection(StripeSettings.SectionName));
var stripeKey = builder.Configuration.GetSection(StripeSettings.SectionName).Get<StripeSettings>()?.SecretKey;
if (!string.IsNullOrEmpty(stripeKey))
{
    Stripe.StripeConfiguration.ApiKey = stripeKey;
}

// --- Push Notifications (VAPID + FCM) ---
builder.Services.Configure<VapidSettings>(
    builder.Configuration.GetSection(VapidSettings.SectionName));
builder.Services.AddHttpClient<IPushNotificationService, PushNotificationService>()
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<IReferralRewardService, StripeCouponRewardService>();
builder.Services.AddHostedService<ReminderSchedulerService>();
builder.Services.AddHostedService<GoalDeadlineNotificationService>();
builder.Services.AddHostedService<SlipAlertSchedulerService>();
builder.Services.AddHostedService<AccountDeletionService>();
builder.Services.AddHostedService<HabitDueDateAdvancementService>();
builder.Services.AddHostedService<DataEncryptionMigrationService>();
builder.Services.AddScoped<ISlipAlertMessageService, AiSlipAlertMessageService>();

// Initialize Firebase Admin SDK for FCM
var firebaseCredJson = builder.Configuration["Firebase:CredentialsJson"];
if (!string.IsNullOrEmpty(firebaseCredJson))
{
    FirebaseAdmin.FirebaseApp.Create(new FirebaseAdmin.AppOptions
    {
        Credential = Google.Apis.Auth.OAuth2.CredentialFactory.FromJson<Google.Apis.Auth.OAuth2.ServiceAccountCredential>(firebaseCredJson).ToGoogleCredential()
    });
}
else
{
    var firebaseCredPath = builder.Configuration["Firebase:CredentialsPath"];
    if (!string.IsNullOrEmpty(firebaseCredPath) && File.Exists(firebaseCredPath))
    {
        FirebaseAdmin.FirebaseApp.Create(new FirebaseAdmin.AppOptions
        {
            Credential = Google.Apis.Auth.OAuth2.CredentialFactory.FromFile<Google.Apis.Auth.OAuth2.ServiceAccountCredential>(firebaseCredPath).ToGoogleCredential()
        });
    }
}

// --- Image Validation ---
builder.Services.AddSingleton<IImageValidationService, ImageValidationService>();

// --- Geo Location ---
builder.Services.AddHttpClient<IGeoLocationService, GeoLocationService>()
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));

// --- JWT Settings ---
builder.Services.Configure<JwtSettings>(
    builder.Configuration.GetSection(JwtSettings.SectionName));

// --- Authentication & Authorization ---
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

// --- AI Provider Configuration ---
builder.Services.Configure<AiSettings>(
    builder.Configuration.GetSection(AiSettings.SectionName));
builder.Services.AddSingleton<AiCompletionClient>();
builder.Services.AddScoped<IAiIntentService, AiIntentService>();
builder.Services.AddScoped<IFactExtractionService, AiFactExtractionService>();
builder.Services.AddScoped<IRoutineAnalysisService, AiRoutineAnalysisService>();
builder.Services.AddScoped<ISummaryService, AiSummaryService>();
builder.Services.AddScoped<IRetrospectiveService, AiRetrospectiveService>();
builder.Services.AddScoped<IGoalReviewService, AiGoalReviewService>();

// --- AI Tool Registration ---
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

// --- In-Memory Cache ---
builder.Services.AddMemoryCache();

// --- Validation ---
builder.Services.AddValidatorsFromAssemblyContaining<CreateHabitCommandValidator>();

// --- MediatR ---
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(typeof(Orbit.Application.Chat.Commands.ProcessUserChatCommand).Assembly);
    cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
});

// --- CORS ---
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:3000"];
var allOrigins = allowedOrigins
    .Concat(["https://claude.ai", "https://claude.com"])
    .ToArray();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(allOrigins)
              .WithHeaders("Authorization", "Content-Type", "Mcp-Session-Id")
              .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS")
              .AllowCredentials();
    });
});

// --- Rate Limiting ---
// NOTE: IMemoryCache-backed rate limiting is not shared across replicas.
// For multi-replica deployments, replace with a distributed store (e.g., Redis).
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("auth", limiterOptions =>
    {
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.PermitLimit = 5;
        limiterOptions.QueueLimit = 0;
        limiterOptions.AutoReplenishment = true;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// --- Request Size Limit ---
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10MB global default
});

// --- MCP Server ---
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

// --- Controllers ---
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

// --- Exception Handling ---
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddProblemDetails();

// --- OpenAPI + Scalar ---
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
});

var app = builder.Build();

// --- Apply Migrations ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
    await db.Database.MigrateAsync();
}

// --- Pipeline ---
app.UseMiddleware<Orbit.Api.Middleware.SecurityHeadersMiddleware>();
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    // Accept only a single forwarded hop. KnownProxies should be configured per deployment environment.
    ForwardLimit = 1
});
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseExceptionHandler();
app.UseCors();

if (app.Environment.IsProduction())
{
    app.UseHttpsRedirection();
}

// MCP selective auth: initialize works without auth, tools require auth
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/mcp") && context.Request.Method == "POST")
    {
        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        context.Request.Body.Position = 0;

        // Allow initialize and notifications without auth
        if (body.Contains("\"method\":\"initialize\"") ||
            body.Contains("\"method\":\"notifications/") ||
            body.Contains("\"method\":\"ping\""))
        {
            await next();
            return;
        }

        // For tool calls, require auth
        var authResult = await context.AuthenticateAsync();
        if (!authResult.Succeeded)
        {
            var scheme = context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? context.Request.Scheme;
            var resourceUrl = $"{scheme}://{context.Request.Host}/.well-known/oauth-protected-resource";
            context.Response.StatusCode = 401;
            context.Response.Headers["WWW-Authenticate"] = $"Bearer resource_metadata=\"{resourceUrl}\"";
            return;
        }
        context.User = authResult.Principal!;
    }
    await next();
});

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapMcp("/mcp");

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.Run();

// Make Program class accessible to integration tests
public partial class Program { }
