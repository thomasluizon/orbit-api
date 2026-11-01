using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AccountDeletionServiceDbTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly OrbitDbContext _dbContext;
    private readonly AccountDeletionService _service;
    private readonly FakeObjectStore _objects = new();
    private readonly FakeObjectStore _legacyObjects = new();

    public AccountDeletionServiceDbTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new SqliteCompatOrbitDbContext(options);
        _dbContext.Database.EnsureCreated();

        var serviceProvider = new ServiceCollection()
            .AddSingleton(_dbContext)
            .AddSingleton<IObjectStorageDeletionService>(_objects)
            .AddSingleton<IObjectStorageDeletionService>(_legacyObjects)
            .AddSingleton<IUnitOfWork>(new UnitOfWork(_dbContext, new DatabaseConnectionSettings()))
            .AddSingleton<IGenericRepository<MarketingContact>>(new GenericRepository<MarketingContact>(_dbContext))
            .AddSingleton<IAccountResetRepository>(new AccountResetRepository(_dbContext, NSubstitute.Substitute.For<Orbit.Domain.Events.IAccountEventCollector>(), NSubstitute.Substitute.For<IObjectStorageService>()))
            .BuildServiceProvider();

        _service = new AccountDeletionService(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AccountDeletionService>.Instance,
            new ConfigurationBuilder().Build());
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task DeleteAllUserDataAsync_RemovesOnlyTargetUsersProactiveDataWithoutRetiredLedger()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        SeedUser(userId, "proactive@example.com");
        SeedUser(otherUserId, "other-proactive@example.com");
        SeedProactiveData(userId);
        SeedProactiveData(otherUserId);
        SeedChatTurns(userId);
        SeedChatTurns(otherUserId);
        await _dbContext.SaveChangesAsync();
        await AssertRetiredLedgerAbsent();

        await new AccountResetRepository(_dbContext, NSubstitute.Substitute.For<Orbit.Domain.Events.IAccountEventCollector>()).DeleteAllUserDataAsync(userId);

        (await _dbContext.ChatTurns.CountAsync(turn => turn.UserId == userId)).Should().Be(0);
        (await _dbContext.ChatTurns.CountAsync(turn => turn.UserId == otherUserId)).Should().Be(2);
        (await _dbContext.ProactiveItems.AnyAsync(p => p.UserId == userId))
            .Should().BeFalse();
        (await _dbContext.ProactiveCalendarDays.AnyAsync(p => p.UserId == userId)).Should().BeFalse();
        (await _dbContext.Users.AnyAsync(u => u.Id == userId)).Should().BeTrue();
        (await _dbContext.ProactiveItems.CountAsync(p => p.UserId == otherUserId)).Should().Be(1);
        (await _dbContext.ProactiveCalendarDays.CountAsync(p => p.UserId == otherUserId)).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_DeletesPastDueUserWithOwnedDataAndIdempotencyLedger()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        SeedDeactivatedUser(userId, "owned@example.com", DateTime.UtcNow.AddDays(-1));
        SeedUser(otherUserId, "other-owned@example.com");
        _dbContext.Tags.Add(Tag.Create(userId, "Fitness", "#ff0000").Value);
        _dbContext.ProcessedRequests.Add(ProcessedRequest.Create(userId, "mutation-key-1", "LogHabitCommand", 0));
        SeedProactiveData(userId);
        SeedProactiveData(otherUserId);
        SeedChatTurns(userId);
        SeedChatTurns(otherUserId);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        await AssertRetiredLedgerAbsent();

        await _service.RunAsync(CancellationToken.None);

        (await _dbContext.Users.AnyAsync(u => u.Id == userId)).Should().BeFalse();
        (await _dbContext.Tags.IgnoreQueryFilters().AnyAsync(t => t.UserId == userId)).Should().BeFalse();
        (await _dbContext.ProcessedRequests.AnyAsync(r => r.UserId == userId)).Should().BeFalse();
        (await _dbContext.ChatTurns.CountAsync(turn => turn.UserId == userId)).Should().Be(0);
        (await _dbContext.ChatTurns.CountAsync(turn => turn.UserId == otherUserId)).Should().Be(2);
        (await _dbContext.ProactiveItems.AnyAsync(p => p.UserId == userId)).Should().BeFalse();
        (await _dbContext.ProactiveCalendarDays.AnyAsync(p => p.UserId == userId)).Should().BeFalse();
        (await _dbContext.Users.AnyAsync(u => u.Id == otherUserId)).Should().BeTrue();
        (await _dbContext.ProactiveItems.CountAsync(p => p.UserId == otherUserId)).Should().Be(1);
        (await _dbContext.ProactiveCalendarDays.CountAsync(p => p.UserId == otherUserId)).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_OptedOutUserDeletion_PreservesAddressOptOut()
    {
        var userId = Guid.NewGuid();
        SeedDeactivatedUser(userId, "PERSON@example.com", DateTime.UtcNow.AddDays(-1));
        var user = _dbContext.Users.Local.Single(u => u.Id == userId);
        user.SetMarketingConsent(false);
        _dbContext.MarketingContacts.Add(MarketingContact.ConfirmWaitlist("person@example.com", "en"));
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        await _service.RunAsync(CancellationToken.None);

        (await _dbContext.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == userId)).Should().BeFalse();
        var contact = await _dbContext.MarketingContacts.SingleAsync();
        contact.Email.Should().Be("person@example.com");
        contact.UnsubscribedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_DeletionWithoutWaitlistContact_CreatesAddressOptOut()
    {
        var userId = Guid.NewGuid();
        SeedDeactivatedUser(userId, "ABSENT@example.com", DateTime.UtcNow.AddDays(-1));
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        await _service.RunAsync(CancellationToken.None);

        (await _dbContext.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == userId)).Should().BeFalse();
        var contact = await _dbContext.MarketingContacts.SingleAsync();
        contact.Email.Should().Be("absent@example.com");
        contact.Source.Should().Be("user");
        contact.UnsubscribedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_DeletesPastDueUsersAttributedAiUsage()
    {
        var userId = Guid.NewGuid();
        SeedDeactivatedUser(userId, "ai-usage@example.com", DateTime.UtcNow.AddDays(-1));
        _dbContext.AiUsageDaily.Add(AiUsageDaily.Create(
            DateOnly.FromDateTime(DateTime.UtcNow),
            "gpt-4.1-mini",
            "astra_chat",
            new AiUsageTotals(1, 0, 100, 50, 150, 0.001m),
            userId));
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        await _service.RunAsync(CancellationToken.None);

        (await _dbContext.AiUsageDaily.AnyAsync(u => u.UserId == userId)).Should().BeFalse();
    }

    [Fact]
    public async Task CleanupStaleSentRecords_RemovesProcessedRequestsOlderThan30Days_KeepsRecent()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId, "retention@example.com");
        await _dbContext.SaveChangesAsync();

        var stale = ProcessedRequest.Create(userId, "stale-key", "LogHabitCommand", 0);
        typeof(ProcessedRequest).GetProperty("CreatedAtUtc")!.SetValue(stale, DateTime.UtcNow.AddDays(-31));
        var recent = ProcessedRequest.Create(userId, "recent-key", "LogHabitCommand", 0);
        _dbContext.ProcessedRequests.AddRange(stale, recent);
        await _dbContext.SaveChangesAsync();

        await _service.CleanupStaleSentRecords(CancellationToken.None);

        var remaining = await _dbContext.ProcessedRequests.Select(r => r.IdempotencyKey).ToListAsync();
        remaining.Should().ContainSingle().Which.Should().Be("recent-key");
    }

    [Fact]
    public async Task RunAsync_DeletesOnlyPastDueDeactivatedUsers_PerUserScope()
    {
        var pastDue = Guid.NewGuid();
        var futureDated = Guid.NewGuid();
        var active = Guid.NewGuid();
        SeedDeactivatedUser(pastDue, "pastdue@example.com", DateTime.UtcNow.AddDays(-1));
        SeedDeactivatedUser(futureDated, "future@example.com", DateTime.UtcNow.AddDays(30));
        SeedUser(active, "active@example.com");
        await _dbContext.SaveChangesAsync();

        await _service.RunAsync(CancellationToken.None);

        var remaining = await _dbContext.Users.IgnoreQueryFilters().Select(u => u.Id).ToListAsync();
        remaining.Should().BeEquivalentTo(new[] { futureDated, active });
    }

    [Fact]
    public async Task RunAsync_DeletesObjectsFromEveryProviderBeforeRemovingUser()
    {
        var userId = Guid.NewGuid();
        var otherKey = $"{Guid.NewGuid()}/keep.png";
        SeedDeactivatedUser(userId, "objects@example.com", DateTime.UtcNow.AddDays(-1));
        await _dbContext.SaveChangesAsync();
        _objects.Keys.UnionWith([$"{userId}/one.png", $"{userId}/nested/two.jpg", otherKey]);
        _legacyObjects.Keys.Add($"{userId}/legacy.png");
        _objects.BeforeDelete = () => _dbContext.Users.IgnoreQueryFilters().Any(u => u.Id == userId).Should().BeTrue();

        await _service.RunAsync(CancellationToken.None);

        _objects.Keys.Should().BeEquivalentTo([otherKey]);
        _legacyObjects.Keys.Should().BeEmpty();
        (await _dbContext.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == userId)).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_StorageFailureRetriesAndCompletesOnlyAfterSuccess()
    {
        var userId = Guid.NewGuid();
        SeedDeactivatedUser(userId, "retry@example.com", DateTime.UtcNow.AddDays(-1));
        await _dbContext.SaveChangesAsync();
        _objects.Keys.Add($"{userId}/one.png");
        _objects.FailuresRemaining = 1;

        await _service.RunAsync(CancellationToken.None);

        _objects.Attempts.Should().Be(2);
        _objects.Keys.Should().BeEmpty();
        (await _dbContext.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == userId)).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_PersistentStorageFailureKeepsUserScheduledForNextRun()
    {
        var userId = Guid.NewGuid();
        SeedDeactivatedUser(userId, "persistent@example.com", DateTime.UtcNow.AddDays(-1));
        _dbContext.Tags.Add(Tag.Create(userId, "Keep until erased", "#ff0000").Value);
        await _dbContext.SaveChangesAsync();
        _legacyObjects.Keys.Add($"{userId}/legacy.png");
        _legacyObjects.FailuresRemaining = 3;

        await _service.RunAsync(CancellationToken.None);

        _legacyObjects.Attempts.Should().Be(3);
        _legacyObjects.Keys.Should().HaveCount(1);
        var retained = await _dbContext.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
        retained.IsDeactivated.Should().BeTrue();
        retained.ScheduledDeletionAt.Should().NotBeNull();
        (await _dbContext.Tags.AnyAsync(t => t.UserId == userId)).Should().BeTrue();

        await _service.RunAsync(CancellationToken.None);

        _legacyObjects.Keys.Should().BeEmpty();
        (await _dbContext.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == userId)).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_DeletesConversationRowsAndPrivateImagesOnlyForErasedAccount()
    {
        var erased = Guid.NewGuid();
        var retained = Guid.NewGuid();
        SeedDeactivatedUser(erased, "conversation-erasure@example.com", DateTime.UtcNow.AddDays(-1));
        SeedUser(retained, "conversation-retained@example.com");
        foreach (var user in new[] { erased, retained })
        {
            var conversation = Guid.NewGuid();
            var imageKey = $"{user}/conversations/{conversation}/image.png";
            _dbContext.ConversationTurns.Add(ConversationTurn.Create(user, Guid.NewGuid(), "user", DateTime.UtcNow,
                null, "Private message", "[]", conversationId: conversation, turnOrder: 1,
                imageObjectKey: imageKey, imageContentType: "image/png", imageSha256: new string('a', 64)).Value);
            _objects.Keys.Add(imageKey);
        }
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        await _service.RunAsync(default);
        (await _dbContext.Users.IgnoreQueryFilters().AnyAsync(user => user.Id == erased)).Should().BeFalse();
        (await _dbContext.ConversationTurns.ToListAsync()).Should().ContainSingle().Which.UserId.Should().Be(retained);
        _objects.Keys.Should().ContainSingle().Which.Should().StartWith($"{retained}/");
    }

    private async Task AssertRetiredLedgerAbsent()
    {
        (await _dbContext.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'SentProactiveCheckins'")
            .SingleAsync()).Should().Be(0);
    }

    private void SeedChatTurns(Guid userId)
    {
        var replay = ChatTurn.Create(userId, new string('A', 64), new string('B', 64), DateTime.UtcNow.AddMinutes(4));
        replay.Complete(ChatStreamEvent.Final(new ChatResponse("Private response", [])).ToJson());
        _dbContext.ChatTurns.AddRange(replay,
            ChatTurn.Create(userId, new string('C', 64), new string('D', 64), DateTime.UtcNow.AddMinutes(4)));
    }

    private void SeedProactiveData(Guid userId)
    {
        var date = new DateOnly(2030, 10, 14);
        _dbContext.ProactiveItems.Add(ProactiveItem.Create(userId,
            Orbit.Domain.Enums.ProactiveTrigger.MorningPlan, date, "{}", "Text.", "Action", "Prompt", DateTime.UtcNow));
        _dbContext.ProactiveCalendarDays.Add(ProactiveCalendarDay.Claim(userId, date));
    }

    private void SeedUser(Guid userId, string email)
    {
        var user = User.Create("Test User", email).Value;
        typeof(User).GetProperty("Id")!.SetValue(user, userId);
        _dbContext.Users.Add(user);
    }

    private void SeedDeactivatedUser(Guid userId, string email, DateTime scheduledDeletion)
    {
        var user = User.Create("Test User", email).Value;
        typeof(User).GetProperty("Id")!.SetValue(user, userId);
        user.Deactivate(scheduledDeletion);
        _dbContext.Users.Add(user);
    }

    private sealed class SqliteCompatOrbitDbContext(DbContextOptions<OrbitDbContext> options)
        : OrbitDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    var defaultSql = property.GetDefaultValueSql();
                    if (defaultSql is not null && defaultSql.Contains("::", StringComparison.Ordinal))
                        property.SetDefaultValueSql(null);
                }

                foreach (var index in entityType.GetIndexes())
                    index.SetFilter(null);
            }
        }
    }
}
