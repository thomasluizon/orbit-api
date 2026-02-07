using Microsoft.EntityFrameworkCore;
using Orbit.Api.Middleware;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// --- Database ---
builder.Services.AddDbContext<OrbitDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// --- Repositories & UoW ---
builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

// --- AI (Claude via Anthropic Messages API) ---
builder.Services.Configure<ClaudeSettings>(
    builder.Configuration.GetSection(ClaudeSettings.SectionName));

builder.Services.AddHttpClient<IAiIntentService, ClaudeIntentService>((sp, client) =>
{
    var settings = builder.Configuration.GetSection(ClaudeSettings.SectionName).Get<ClaudeSettings>()!;
    client.BaseAddress = new Uri("https://api.anthropic.com/");
    client.DefaultRequestHeaders.Add("x-api-key", settings.ApiKey);
    client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
});

// --- MediatR ---
builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssembly(typeof(Orbit.Application.Chat.Commands.ProcessUserChatCommand).Assembly));

// --- Controllers + OpenAPI ---
builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

// --- Ensure DB + Seed dev user ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
    await db.Database.EnsureCreatedAsync();

    var devUserId = DevUserMiddleware.DevUserId;
    if (!await db.Users.AnyAsync(u => u.Id == devUserId))
    {
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "Users" ("Id", "Name", "Email", "CreatedAtUtc")
            VALUES ({devUserId}, 'Dev User', 'dev@orbit.local', {DateTime.UtcNow})
            """);
    }
}

// --- Pipeline ---
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();
app.UseMiddleware<DevUserMiddleware>();
app.MapControllers();

app.Run();
