using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Asp.Versioning;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Authorization;
using Orbit.Api.Controllers;
using Orbit.Api.RateLimiting;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Profile.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Controllers;

public sealed class ExportHttpTests
{
    private static readonly string[] Collections = ["habits", "goals", "tags", "notifications", "checklistTemplates",
        "achievements", "streakFreezes", "referrals", "apiKeys", "friendships", "cheers", "blockedUsers", "reports",
        "friendFeedEvents", "proactiveItems", "notionTasks", "conversations", "conversationOffers"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProfileExport_DownloadsCompleteLargeDocumentWithSynchronousIoDisabled(bool brotli)
    {
        await using var factory = new ExportApiFactory();
        using var client = factory.CreateClient();
        factory.Server.AllowSynchronousIO.Should().BeFalse();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/profile/export");
        if (brotli) request.Headers.AcceptEncoding.ParseAdd("br");
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertDocument(response, brotli);
    }

    private static async Task AssertDocument(HttpResponseMessage response, bool brotli)
    {
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        await using var body = await response.Content.ReadAsStreamAsync();
        await using var decoded = brotli ? new BrotliStream(body, CompressionMode.Decompress, leaveOpen: true) : null;
        if (brotli) response.Content.Headers.ContentEncoding.Should().ContainSingle().Which.Should().Be("br");
        else response.Content.Headers.ContentEncoding.Should().BeEmpty();
        using var json = await JsonDocument.ParseAsync(decoded is null ? body : decoded);
        json.RootElement.EnumerateObject().Select(property => property.Name).Should().Equal(
            new[] { "exportedAtUtc", "account", "settings", "subscription" }
                .Concat(Collections.Take(14)).Concat(["proactiveItems", "notionConnection", "notionTasks", "conversations", "conversationOffers"]));
        foreach (var name in Collections) json.RootElement.GetProperty(name).ValueKind.Should().Be(JsonValueKind.Array, name);
        json.RootElement.GetProperty("habits").GetArrayLength().Should().Be(150);
        json.RootElement.GetProperty("habits")[0].GetProperty("logs").GetArrayLength().Should().Be(10);
        json.RootElement.GetProperty("conversations").GetArrayLength().Should().Be(2000);
    }

    private sealed class ExportApiFactory : WebApplicationFactory<ProfileController>
    {
        private readonly SqliteOrbitDbContextFactory _database = new();
        private readonly OrbitDbContext _rateDatabase = new(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        private readonly Guid _userId;
        internal MutableClock Clock { get; } = new();

        internal ExportApiFactory(bool seedLargeAccount = true)
        {
            var user = User.Create("Export", "export-http@example.test").Value;
            _userId = user.Id;
            _database.Context.Users.Add(user);
            if (seedLargeAccount)
            {
                for (var index = 0; index < 150; index++)
                {
                    var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Read " + index,
                        FrequencyUnit.Day, 1, new DateOnly(2026, 6, 1))).Value;
                    _database.Context.Habits.Add(habit);
                    for (var day = 0; day < 10; day++)
                        _database.Context.HabitLogs.Add(habit.Log(new DateOnly(2026, 6, 1).AddDays(day),
                            string.Concat(Enumerable.Range(0, 16).Select(_ => Guid.NewGuid().ToString()))).Value);
                }
                for (var index = 0; index < 2000; index++)
                    _database.Context.ConversationTurns.Add(ConversationTurn.Create(user.Id, Guid.NewGuid(), "user",
                        DateTime.UtcNow.AddSeconds(index), null, new string('x', 1024), "[]").Value);
            }
            _database.Context.SaveChanges();
            _database.Context.ChangeTracker.Clear();
        }

        protected override IHostBuilder CreateHostBuilder() => Host.CreateDefaultBuilder()
            .UseEnvironment("Staging")
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { [EvalAccountAccess.AccountIdsKey] = _userId.ToString() }))
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHost(web => web.ConfigureServices(services =>
            {
                services.AddControllers().AddApplicationPart(typeof(ProfileController).Assembly);
                services.AddApiVersioning(options =>
                {
                    options.DefaultApiVersion = new ApiVersion(1, 0);
                    options.AssumeDefaultVersionWhenUnspecified = true;
                    options.ApiVersionReader = new HeaderApiVersionReader("api-version");
                }).AddMvc();
                services.AddResponseCompression(options =>
                {
                    options.EnableForHttps = true;
                    options.Providers.Add<BrotliCompressionProvider>();
                });
                services.AddAuthentication("JwtBearer").AddScheme<AuthenticationSchemeOptions, ExportAuthenticationHandler>("JwtBearer", _ => { });
                services.AddAuthorization(options => options.AddPolicy(EvalAccountRequirement.PolicyName,
                    policy => policy.RequireAuthenticatedUser().AddRequirements(new EvalAccountRequirement())));
                services.AddSingleton<IAuthorizationHandler, EvalAccountAuthorizationHandler>();
                services.AddSingleton(_database.Context);
                services.AddSingleton(typeof(IGenericRepository<>), typeof(GenericRepository<>));
                services.AddSingleton<ExportUserDataRepositories>();
                services.AddMediatR(options => options.RegisterServicesFromAssemblyContaining<StreamUserDataQuery>());
                var dates = Substitute.For<IUserDateService>();
                dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 6, 1));
                services.AddSingleton(dates);
                var progress = Substitute.For<IGoalProgressReadSyncer>();
                progress.ComputeFreshValuesAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
                    .Returns(new Dictionary<Guid, int>());
                services.AddSingleton(progress);
                services.AddSingleton<IDistributedRateLimitService>(new DistributedRateLimitService(_rateDatabase, Clock));
                services.AddSingleton(Substitute.For<IAuthSessionService>());
                services.AddSingleton<BffClientIpResolver>();
            }).Configure(app =>
            {
                app.Use(async (context, next) =>
                {
                    if (!context.Request.Headers.ContainsKey("X-Test-User"))
                        context.Request.Headers["X-Test-User"] = _userId.ToString();
                    await next(context);
                });
                app.UseResponseCompression();
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            }));

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            _database.Dispose();
            await _rateDatabase.DisposeAsync();
        }
    }

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 6, 1, 12, 0, 30, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class ExportAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Request.Headers["X-Test-User"].ToString()),
                new Claim("auth_method", "jwt")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
