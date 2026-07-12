using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Gamification.Backfill;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Orchestration guarantees of the one-time XP-award-log backfill hosted service: it defers the sweep
/// behind a startup delay, skips entirely once the <c>XpAwardLogBackfillComplete</c> flag is set, and
/// on a clean run writes the flag and logs the outcome. Runs the real backfill service and repositories
/// against the EF in-memory provider (fresh contexts share a named store, mirroring the request scope).
/// </summary>
public sealed class XpAwardLogBackfillHostedServiceTests : IDisposable
{
    private const string BackfillFlag = "XpAwardLogBackfillComplete";

    private readonly string _dbName = $"XpBackfillHosted_{Guid.NewGuid()}";
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RecordingLogger _logger = new();

    public XpAwardLogBackfillHostedServiceTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new DatabaseConnectionSettings());
        services.AddScoped(_ => CreateContext(_dbName));
        services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<XpAwardLogBackfillService>();
        _provider = services.BuildServiceProvider();
        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
    }

    public void Dispose() => _provider.Dispose();

    [Fact]
    public async Task StartAsync_DefersBackfillBehindStartupDelay_DoesNotRunImmediately()
    {
        await SeedUserAsync(50);
        var sut = CreateSut();

        await sut.StartAsync(CancellationToken.None);
        try
        {
            await using var probe = CreateContext(_dbName);
            (await probe.XpAwardLogs.CountAsync()).Should().Be(0);
            (await probe.AppConfigs.AnyAsync(c => c.Key == BackfillFlag)).Should().BeFalse();
            _logger.Entries.Should().BeEmpty();
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }

        await using var verify = CreateContext(_dbName);
        (await verify.XpAwardLogs.CountAsync()).Should().Be(0);
        (await verify.AppConfigs.AnyAsync(c => c.Key == BackfillFlag)).Should().BeFalse();
    }

    [Fact]
    public async Task RunBackfillAsync_FlagAlreadySet_SkipsBackfillAndLeavesDataUntouched()
    {
        await SeedUserAsync(50);
        await using (var seed = CreateContext(_dbName))
        {
            seed.AppConfigs.Add(AppConfig.Create(BackfillFlag, "true", "seeded"));
            await seed.SaveChangesAsync();
        }

        await CreateSut().RunBackfillAsync(CancellationToken.None);

        await using var verify = CreateContext(_dbName);
        (await verify.XpAwardLogs.CountAsync()).Should().Be(0);
        (await verify.AppConfigs.CountAsync(c => c.Key == BackfillFlag)).Should().Be(1);
        _logger.Entries.Should().ContainSingle()
            .Which.Should().Match<LogEntry>(e => e.Level == LogLevel.Information && e.EventId == 1);
    }

    [Fact]
    public async Task RunBackfillAsync_NotYetRun_BackfillsWritesFlagAndLogsCompletion()
    {
        await SeedUserAsync(50);

        await CreateSut().RunBackfillAsync(CancellationToken.None);

        await using var verify = CreateContext(_dbName);
        (await verify.AppConfigs.AnyAsync(c => c.Key == BackfillFlag)).Should().BeTrue();
        var xpRows = await verify.XpAwardLogs.ToListAsync();
        xpRows.Should().ContainSingle();
        xpRows[0].Amount.Should().Be(50);
        xpRows[0].Source.Should().Be(XpAwardSource.Reconciliation);

        var completion = _logger.Entries.Should()
            .ContainSingle(e => e.Level == LogLevel.Information && e.EventId == 2).Subject;
        completion.Message.Should().Contain("1");
    }

    [Fact]
    public async Task RunBackfillAsync_ScopeCreationThrows_LogsErrorAndDoesNotRethrow()
    {
        var faultyScopeFactory = Substitute.For<IServiceScopeFactory>();
        faultyScopeFactory.CreateScope().Returns(_ => throw new InvalidOperationException("scope unavailable"));
        var sut = CreateSut(faultyScopeFactory);

        var act = async () => await sut.RunBackfillAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.EventId == 3);
    }

    private XpAwardLogBackfillHostedService CreateSut(IServiceScopeFactory? scopeFactory = null) =>
        new(scopeFactory ?? _scopeFactory, _logger);

    private async Task SeedUserAsync(int totalXp)
    {
        var user = User.Create("Test", $"{Guid.NewGuid():N}@example.com").Value;
        user.AddXp(totalXp);
        await using var seed = CreateContext(_dbName);
        seed.Users.Add(user);
        await seed.SaveChangesAsync();
    }

    private static OrbitDbContext CreateContext(string dbName) =>
        new(new DbContextOptionsBuilder<OrbitDbContext>().UseInMemoryDatabase(dbName).Options);

    private sealed record LogEntry(LogLevel Level, int EventId, string Message);

    private sealed class RecordingLogger : ILogger<XpAwardLogBackfillHostedService>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, eventId.Id, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
