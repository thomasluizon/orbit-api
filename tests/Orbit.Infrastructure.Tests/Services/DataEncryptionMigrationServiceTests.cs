using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the one-time encryption migration's safety invariants: it never marks itself
/// complete in passthrough mode (so a later configured run still executes), and a failing
/// batch halts the run without skipping rows or marking complete.
/// </summary>
public class DataEncryptionMigrationServiceTests
{
    private const string MigrationFlag = "EncryptionMigrationComplete_v2";
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task RunMigrationAsync_NotConfigured_DoesNotSetCompletionFlag()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var encryption = Substitute.For<IEncryptionService>();
        encryption.IsConfigured.Returns(false);

        var service = CreateService(dbContext, encryption);
        await service.RunMigrationAsync(CancellationToken.None);

        (await dbContext.AppConfigs.AnyAsync(c => c.Key == MigrationFlag)).Should().BeFalse();
    }

    [Fact]
    public async Task RunMigrationAsync_Configured_SetsCompletionFlag()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var encryption = Substitute.For<IEncryptionService>();
        encryption.IsConfigured.Returns(true);

        var service = CreateService(dbContext, encryption);
        await service.RunMigrationAsync(CancellationToken.None);

        (await dbContext.AppConfigs.AnyAsync(c => c.Key == MigrationFlag)).Should().BeTrue();
    }

    [Fact]
    public async Task RunMigrationAsync_AlreadyComplete_IsNoOp()
    {
        await using var dbContext = CreateInMemoryDbContext();
        dbContext.AppConfigs.Add(AppConfig.Create(MigrationFlag, "true"));
        await dbContext.SaveChangesAsync();
        var encryption = Substitute.For<IEncryptionService>();
        encryption.IsConfigured.Returns(true);

        var service = CreateService(dbContext, encryption);
        await service.RunMigrationAsync(CancellationToken.None);

        (await dbContext.AppConfigs.CountAsync(c => c.Key == MigrationFlag)).Should().Be(1);
    }

    [Fact]
    public async Task RunMigrationAsync_FailingBatch_HaltsWithoutSkippingRowsOrMarkingComplete()
    {
        var dbName = $"DataEncryptionMigrationServiceTests_{Guid.NewGuid()}";
        await using (var seedContext = CreateInMemoryDbContext(dbName))
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            for (int i = 0; i < 120; i++)
                seedContext.Habits.Add(Habit.Create(new HabitCreateParams(UserId, $"Habit {i}", FrequencyUnit.Day, 1, DueDate: today)).Value);
            await seedContext.SaveChangesAsync();
        }

        await using var throwingContext = new ThrowingOrbitDbContext(BuildOptions(dbName));
        var encryption = Substitute.For<IEncryptionService>();
        encryption.IsConfigured.Returns(true);

        var service = CreateService(throwingContext, encryption);
        await service.RunMigrationAsync(CancellationToken.None);

        throwingContext.SaveAttempts.Should().Be(1);
        (await throwingContext.AppConfigs.AnyAsync(c => c.Key == MigrationFlag)).Should().BeFalse();
    }

    private static DbContextOptions<OrbitDbContext> BuildOptions(string dbName) =>
        new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

    private static OrbitDbContext CreateInMemoryDbContext(string? dbName = null) =>
        new(BuildOptions(dbName ?? $"DataEncryptionMigrationServiceTests_{Guid.NewGuid()}"));

    private static DataEncryptionMigrationService CreateService(
        OrbitDbContext dbContext, IEncryptionService encryption)
    {
        var serviceProvider = new ServiceCollection()
            .AddSingleton(dbContext)
            .BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        return new DataEncryptionMigrationService(
            scopeFactory, encryption, NullLogger<DataEncryptionMigrationService>.Instance);
    }

    private sealed class ThrowingOrbitDbContext(DbContextOptions<OrbitDbContext> options) : OrbitDbContext(options)
    {
        public int SaveAttempts { get; private set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            throw new DbUpdateException("simulated batch failure");
        }
    }
}
