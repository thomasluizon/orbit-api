using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.Idempotency;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Services;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Services;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Controllers;

internal sealed class BulkHabitsPipelineHost : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly RequestDelegate _pipeline;

    internal BulkHabitsPipelineHost(Func<OrbitDbContext> database, BulkRequestProbe probe, DateOnly today)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(HabitsController).Assembly)
            .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddAuthorization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped(_ => database());
        builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
        AddTimedRepository<Orbit.Domain.Entities.Habit>(builder.Services, probe);
        AddTimedRepository<Orbit.Domain.Entities.HabitLog>(builder.Services, probe);
        AddTimedRepository<Orbit.Domain.Entities.User>(builder.Services, probe);
        AddTimedRepository<Orbit.Domain.Entities.Notification>(builder.Services, probe);
        AddTimedRepository<Orbit.Domain.Entities.Goal>(builder.Services, probe);
        AddTimedRepository<Orbit.Domain.Entities.StreakFreeze>(builder.Services, probe);
        AddTimedRepository<Orbit.Domain.Entities.UserAchievement>(builder.Services, probe);
        AddTimedRepository<Orbit.Domain.Entities.XpAwardLog>(builder.Services, probe);
        builder.Services.AddSingleton(new DatabaseConnectionSettings());
        builder.Services.AddScoped<IUnitOfWork>(sp => probe.Wrap<IUnitOfWork>(
            new UnitOfWork(sp.GetRequiredService<OrbitDbContext>(), sp.GetRequiredService<DatabaseConnectionSettings>(),
                scheduleSnapshots: sp.GetRequiredService<HabitScheduleSnapshotStore>()),
            method => method.Name switch
            {
                nameof(IUnitOfWork.SaveChangesAsync) => "save",
                nameof(IUnitOfWork.AcquireAdvisoryLockAsync) or nameof(IUnitOfWork.AcquireUserRowLockAsync) => "lock wait",
                nameof(IUnitOfWork.ExecuteInTransactionAsync) => "processing in transaction",
                _ => null
            }));
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton<TimeProvider>(new FixedTime(today));
        builder.Services.AddScoped<IUserDateService, UserDateService>();
        builder.Services.AddScoped<UserStreakRepositories>();
        builder.Services.AddScoped<IUserStreakService, UserStreakService>();
        builder.Services.AddScoped<HabitScheduleSnapshotStore>();
        builder.Services.AddScoped<GamificationRepositories>();
        builder.Services.AddScoped<IGamificationService, GamificationService>();
        builder.Services.AddScoped<IFoundingAchievementReader, FoundingAchievementReader>();
        builder.Services.AddScoped<IXpAwarder, XpAwarder>();
        builder.Services.AddSingleton(Substitute.For<Hangfire.IBackgroundJobClient>());
        builder.Services.AddSingleton(Substitute.For<IFeatureFlagService>());
        builder.Services.AddScoped<IGoalCompletionService, GoalCompletionService>();
        builder.Services.AddScoped<BulkLogServices>();
        builder.Services.AddSingleton<MemoryCache>(_ => new(new MemoryCacheOptions()));
        builder.Services.AddScoped<IMemoryCache>(sp => new VersionedMemoryCache(sp.GetRequiredService<MemoryCache>(),
            probe.Wrap<IUserCacheVersions>(new UserCacheVersions(sp.GetRequiredService<OrbitDbContext>()), _ => "cache invalidation")));
        builder.Services.AddScoped<IIdempotencyContext, HttpIdempotencyContext>();
        builder.Services.AddScoped<IIdempotencyStore>(sp => probe.Wrap<IIdempotencyStore>(
            new IdempotencyStore(sp.GetRequiredService<OrbitDbContext>()),
            method => method.Name == nameof(IIdempotencyStore.FindResponseBodyAsync) ? "idempotency read" : null));
        builder.Services.AddSingleton(probe.Wrap<IValidator<BulkLogHabitsCommand>>(new BulkLogHabitsCommandValidator(),
            method => method.Name == "ValidateAsync" ? "validation" : null));
        builder.Services.AddSingleton(probe.Wrap<IValidator<BulkSkipHabitsCommand>>(new BulkSkipHabitsCommandValidator(),
            method => method.Name == "ValidateAsync" ? "validation" : null));
        builder.Services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssemblyContaining<BulkLogHabitsCommand>();
            configuration.AddOpenBehavior(typeof(ConcurrencyRetryBehavior<,>));
            configuration.AddOpenBehavior(typeof(ValidationBehavior<,>));
            configuration.AddOpenBehavior(typeof(IdempotencyBehavior<,>));
        });
        _application = builder.Build();
        var routes = new ApplicationBuilder(_application.Services);
        routes.UseRouting();
        routes.UseAuthorization();
        routes.UseEndpoints(endpoints => endpoints.MapControllers());
        _pipeline = routes.Build();
    }

    internal async Task<HttpContext> SendAsync(Guid userId, string operation, string body, string key)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"))
        };
        context.Request.Method = "POST";
        context.Request.Path = $"/api/habits/bulk/{operation}";
        context.Request.ContentType = "application/json";
        context.Request.Headers["Idempotency-Key"] = key;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = context.Request.Body.Length;
        context.Response.Body = new MemoryStream();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        accessor.HttpContext = context;
        try { await _pipeline(context); }
        finally { accessor.HttpContext = previous; }
        return context;
    }

    private static void AddTimedRepository<T>(IServiceCollection services, BulkRequestProbe probe) where T : Orbit.Domain.Common.Entity =>
        services.AddScoped<IGenericRepository<T>>(sp => probe.Wrap<ITimedRepository<T>>(
            new GenericRepository<T>(sp.GetRequiredService<OrbitDbContext>()),
            method => method.Name is "HasTrackedEntities" or "AddAsync" or "Update" or "Remove" or "RemoveRange" ? null : "loads"));

    internal interface ITimedRepository<T> : IGenericRepository<T>, ITrackedEntityLookup<T> where T : Orbit.Domain.Common.Entity;

    public ValueTask DisposeAsync() => _application.DisposeAsync();

    private sealed class FixedTime(DateOnly today) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(today.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc));
    }
}
