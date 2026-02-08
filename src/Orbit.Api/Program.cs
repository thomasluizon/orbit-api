using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

// --- Database ---
builder.Services.AddDbContext<OrbitDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// --- Repositories & UoW ---
builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

// --- Password & Token Services ---
builder.Services.AddScoped<IPasswordHasher, PasswordHasher>();
builder.Services.AddScoped<ITokenService, JwtTokenService>();

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
var aiProvider = builder.Configuration.GetValue<string>("AiProvider") ?? "Ollama";

if (aiProvider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
{
    // Gemini (Google) API
    builder.Services.Configure<GeminiSettings>(
        builder.Configuration.GetSection(GeminiSettings.SectionName));

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

// --- MediatR ---
builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssembly(typeof(Orbit.Application.Chat.Commands.ProcessUserChatCommand).Assembly));

// --- Controllers ---
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

// --- Swagger UI with JWT Support ---
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Orbit API",
        Version = "v1",
        Description = "AI-powered life management system with JWT authentication"
    });

    // Show enum names instead of integers in Swagger UI
    options.UseInlineDefinitionsForEnums();

    // JWT Bearer Authentication
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Enter your token in the text input below.",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// --- Ensure Database Exists ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// --- Pipeline ---
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Orbit API v1");
        options.RoutePrefix = string.Empty; // Serve at root
    });
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

// Make Program class accessible to integration tests
public partial class Program { }
