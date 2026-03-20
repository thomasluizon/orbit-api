using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Orbit.Api.Middleware;
using Orbit.Api.OpenApi;
using Orbit.Application.Behaviors;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// --- Database ---
builder.Services.AddDbContext<OrbitDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// --- Repositories & UoW ---
builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<IAppConfigService, AppConfigService>();
builder.Services.AddScoped<IUserDateService, UserDateService>();
builder.Services.AddScoped<IPayGateService, Orbit.Application.Common.PayGateService>();

// --- Token Service ---
builder.Services.AddScoped<ITokenService, JwtTokenService>();

// --- Supabase (OAuth token validation) ---
builder.Services.AddHttpClient("Supabase", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Supabase:Url"]!);
    client.DefaultRequestHeaders.Add("apikey", builder.Configuration["Supabase:AnonKey"]!);
});

// --- Resend (Email) ---
builder.Services.Configure<ResendSettings>(
    builder.Configuration.GetSection(ResendSettings.SectionName));

builder.Services.AddHttpClient("Resend", client =>
{
    client.BaseAddress = new Uri("https://api.resend.com");
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {builder.Configuration["Resend:ApiKey"]}");
});

builder.Services.AddScoped<IEmailService, ResendEmailService>();

// --- Stripe ---
builder.Services.Configure<StripeSettings>(
    builder.Configuration.GetSection(StripeSettings.SectionName));

// --- Push Notifications (VAPID + FCM) ---
builder.Services.Configure<VapidSettings>(
    builder.Configuration.GetSection(VapidSettings.SectionName));
builder.Services.AddScoped<IPushNotificationService, PushNotificationService>();
builder.Services.AddHostedService<ReminderSchedulerService>();

// Initialize Firebase Admin SDK for FCM
var firebaseCredJson = builder.Configuration["Firebase:CredentialsJson"];
if (!string.IsNullOrEmpty(firebaseCredJson))
{
    FirebaseAdmin.FirebaseApp.Create(new FirebaseAdmin.AppOptions
    {
        Credential = Google.Apis.Auth.OAuth2.GoogleCredential.FromJson(firebaseCredJson)
    });
}
else
{
    var firebaseCredPath = builder.Configuration["Firebase:CredentialsPath"];
    if (!string.IsNullOrEmpty(firebaseCredPath) && File.Exists(firebaseCredPath))
    {
        FirebaseAdmin.FirebaseApp.Create(new FirebaseAdmin.AppOptions
        {
            Credential = Google.Apis.Auth.OAuth2.GoogleCredential.FromFile(firebaseCredPath)
        });
    }
}

// --- OpenTelemetry (Grafana Cloud) ---
var otelEndpoint = builder.Configuration["Grafana:OtlpEndpoint"];
if (!string.IsNullOrEmpty(otelEndpoint))
{
    var otelInstanceId = builder.Configuration["Grafana:InstanceId"] ?? "";
    var otelToken = builder.Configuration["Grafana:ApiToken"] ?? "";
    var otelAuth = Convert.ToBase64String(
        System.Text.Encoding.UTF8.GetBytes($"{otelInstanceId}:{otelToken}"));

    var resourceBuilder = ResourceBuilder.CreateDefault()
        .AddService("orbit-api", serviceVersion: "1.0.0");

    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing => tracing
            .SetResourceBuilder(resourceBuilder)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddEntityFrameworkCoreInstrumentation()
            .AddOtlpExporter(o =>
            {
                o.Endpoint = new Uri(otelEndpoint);
                o.Headers = $"Authorization=Basic {otelAuth}";
                o.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
            }))
        .WithMetrics(metrics => metrics
            .SetResourceBuilder(resourceBuilder)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(o =>
            {
                o.Endpoint = new Uri(otelEndpoint);
                o.Headers = $"Authorization=Basic {otelAuth}";
                o.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
            }));

    builder.Logging.AddOpenTelemetry(logging =>
    {
        logging.SetResourceBuilder(resourceBuilder);
        logging.IncludeScopes = true;
        logging.IncludeFormattedMessage = true;
        logging.AddOtlpExporter(o =>
        {
            o.Endpoint = new Uri(otelEndpoint);
            o.Headers = $"Authorization=Basic {otelAuth}";
            o.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
        });
    });
}

// --- Image Validation ---
builder.Services.AddSingleton<IImageValidationService, ImageValidationService>();

// --- Geo Location ---
builder.Services.AddHttpClient<IGeoLocationService, GeoLocationService>();

// --- JWT Settings ---
builder.Services.Configure<JwtSettings>(
    builder.Configuration.GetSection(JwtSettings.SectionName));

// --- Authentication & Authorization ---
var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>()!;

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
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
});

builder.Services.AddAuthorization();

// --- AI Provider Configuration ---
// Always configure Gemini settings (used for fact extraction even with Ollama)
builder.Services.Configure<GeminiSettings>(
    builder.Configuration.GetSection(GeminiSettings.SectionName));

var aiProvider = builder.Configuration.GetValue<string>("AiProvider") ?? "Ollama";

if (aiProvider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
{
    // Gemini (Google) API
    builder.Services.AddHttpClient<IAiIntentService, GeminiIntentService>();
}
else
{
    // Ollama (Local LLM) - Default
    builder.Services.Configure<OllamaSettings>(
        builder.Configuration.GetSection(OllamaSettings.SectionName));

    builder.Services.AddHttpClient<IAiIntentService, OllamaIntentService>((sp, client) =>
    {
        var settings = builder.Configuration.GetSection(OllamaSettings.SectionName).Get<OllamaSettings>()
                       ?? new OllamaSettings();
        client.BaseAddress = new Uri(settings.BaseUrl);
    });
}

// Fact extraction always uses Gemini (structured output reliability)
builder.Services.AddHttpClient<IFactExtractionService, GeminiFactExtractionService>();

// Routine analysis always uses Gemini (structured output reliability)
builder.Services.AddHttpClient<IRoutineAnalysisService, GeminiRoutineAnalysisService>();

// Daily summary always uses Gemini (free-text generation)
builder.Services.AddHttpClient<ISummaryService, GeminiSummaryService>();

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
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

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
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseExceptionHandler();
app.UseCors();

if (!app.Environment.IsProduction())
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.Run();

// Make Program class accessible to integration tests
public partial class Program { }
