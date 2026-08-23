using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Backfill;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class AchievementEligibilityReconciliationHostedServiceTests : IDisposable
{
    private const string CompletionKey = "AchievementEligibilityReconciliationComplete";

    private readonly string _dbName = $"AchievementReconciliationHosted_{Guid.NewGuid()}";
    private readonly IAchievementEligibilityReconciliationService _reconciliationService =
        Substitute.For<IAchievementEligibilityReconciliationService>();
    private readonly RecordingLogger _logger = new();
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;

    public AchievementEligibilityReconciliationHostedServiceTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new DatabaseConnectionSettings());
        services.AddScoped(_ => CreateContext(_dbName));
        services.AddScoped(_ => _reconciliationService);
        _provider = services.BuildServiceProvider();
        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        _reconciliationService.ReconcileAllAsync(Arg.Any<CancellationToken>())
            .Returns(new AchievementEligibilityReconciliationResult(2, 5));
    }

    public void Dispose() => _provider.Dispose();

    [Fact]
    public async Task RunReconciliationAsync_CompletionMarkerExists_SkipsSweep()
    {
        await using (var seed = CreateContext(_dbName))
        {
            seed.AppConfigs.Add(AppConfig.Create(CompletionKey, "true"));
            await seed.SaveChangesAsync();
        }

        await CreateSut().RunReconciliationAsync(CancellationToken.None);

        await _reconciliationService.DidNotReceive().ReconcileAllAsync(Arg.Any<CancellationToken>());
        _logger.Entries.Should().ContainSingle()
            .Which.Should().Match<LogEntry>(entry => entry.Level == LogLevel.Information && entry.EventId == 1);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "pro")]
    public async Task RunReconciliationAsync_FreeTierUnavailable_DefersWithoutCompletionMarker(
        bool enabled,
        string? planRequirement)
    {
        await SeedFlagAsync(enabled, planRequirement);

        await CreateSut().RunReconciliationAsync(CancellationToken.None);

        await using var verify = CreateContext(_dbName);
        (await verify.AppConfigs.AnyAsync(config => config.Key == CompletionKey)).Should().BeFalse();
        await _reconciliationService.DidNotReceive().ReconcileAllAsync(Arg.Any<CancellationToken>());
        _logger.Entries.Should().ContainSingle()
            .Which.Should().Match<LogEntry>(entry => entry.Level == LogLevel.Information && entry.EventId == 2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Free")]
    public async Task RunReconciliationAsync_FreeTierAvailable_RunsSweepWritesMarkerAndLogsBothCounts(
        string? planRequirement)
    {
        await SeedFlagAsync(enabled: true, planRequirement);

        await CreateSut().RunReconciliationAsync(CancellationToken.None);

        await using var verify = CreateContext(_dbName);
        (await verify.AppConfigs.CountAsync(config => config.Key == CompletionKey)).Should().Be(1);
        await _reconciliationService.Received(1).ReconcileAllAsync(Arg.Any<CancellationToken>());
        var completion = _logger.Entries.Should()
            .ContainSingle(entry => entry.Level == LogLevel.Information && entry.EventId == 3).Subject;
        completion.Message.Should().Contain("5").And.Contain("2");
    }

    [Fact]
    public async Task RunReconciliationAsync_SweepFails_LogsErrorAndLeavesMarkerUnset()
    {
        await SeedFlagAsync(enabled: true);
        _reconciliationService.ReconcileAllAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("reconciliation failed"));

        var act = async () => await CreateSut().RunReconciliationAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        await using var verify = CreateContext(_dbName);
        (await verify.AppConfigs.AnyAsync(config => config.Key == CompletionKey)).Should().BeFalse();
        _logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error && entry.EventId == 4);
    }

    [Fact]
    public async Task RunReconciliationAsync_RunTwice_SecondRunSkipsSweep()
    {
        await SeedFlagAsync(enabled: true);
        var sut = CreateSut();

        await sut.RunReconciliationAsync(CancellationToken.None);
        await sut.RunReconciliationAsync(CancellationToken.None);

        await _reconciliationService.Received(1).ReconcileAllAsync(Arg.Any<CancellationToken>());
        await using var verify = CreateContext(_dbName);
        (await verify.AppConfigs.CountAsync(config => config.Key == CompletionKey)).Should().Be(1);
        _logger.Entries.Should().Contain(entry => entry.EventId == 3);
        _logger.Entries.Should().Contain(entry => entry.EventId == 1);
    }

    private AchievementEligibilityReconciliationHostedService CreateSut() =>
        new(_scopeFactory, _logger);

    private async Task SeedFlagAsync(bool enabled, string? planRequirement = null)
    {
        await using var seed = CreateContext(_dbName);
        seed.AppFeatureFlags.Add(AppFeatureFlag.Create(
            FeatureFlagKeys.GamificationFreeTier,
            enabled,
            planRequirement));
        await seed.SaveChangesAsync();
    }

    private static OrbitDbContext CreateContext(string dbName) =>
        new(new DbContextOptionsBuilder<OrbitDbContext>().UseInMemoryDatabase(dbName).Options);

    private sealed record LogEntry(LogLevel Level, int EventId, string Message);

    private sealed class RecordingLogger : ILogger<AchievementEligibilityReconciliationHostedService>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, eventId.Id, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
