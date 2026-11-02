using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Connections;
using Orbit.Application.Auth.Jobs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Application.Subscriptions.Services;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AccountDeletionServiceDbTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly OrbitDbContext _dbContext;
    private readonly AccountDeletionService _service;
    private readonly FakeObjectStore _objects = new();
    private readonly FakeObjectStore _legacyObjects = new();

    private readonly IBillingService _billing = Substitute.For<IBillingService>();
    private readonly IGoogleTokenService _google = Substitute.For<IGoogleTokenService>();
    private readonly Microsoft.Extensions.Logging.ILogger<AccountDeletionService> _logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<AccountDeletionService>>();
    private readonly IAppleIdentityService _apple = Substitute.For<IAppleIdentityService>();

    public AccountDeletionServiceDbTests()
    {
        _logger.IsEnabled(Arg.Any<Microsoft.Extensions.Logging.LogLevel>()).Returns(true);
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new SqliteCompatOrbitDbContext(options);
        _dbContext.Database.EnsureCreated();

        var serviceProvider = new ServiceCollection()
            .AddSingleton(_dbContext)
            .AddSingleton(_billing)
            .AddSingleton(_google)
            .AddSingleton(_apple)
            .AddSingleton<IGenericRepository<User>>(new GenericRepository<User>(_dbContext))
            .AddSingleton<RevokeAppleTokenOnDeletionJob>()
            .AddSingleton<IObjectStorageDeletionService>(_objects)
            .AddSingleton<IObjectStorageDeletionService>(_legacyObjects)
            .AddSingleton<IUnitOfWork>(new UnitOfWork(_dbContext, new DatabaseConnectionSettings()))
            .AddSingleton<IGenericRepository<MarketingContact>>(new GenericRepository<MarketingContact>(_dbContext))
            .AddSingleton<IAccountResetRepository>(new AccountResetRepository(_dbContext, NSubstitute.Substitute.For<Orbit.Domain.Events.IAccountEventCollector>(), NSubstitute.Substitute.For<IObjectStorageService>()))
            .BuildServiceProvider();

        _service = new AccountDeletionService(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _logger,
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
        AccountRemovalRows.Seed(_dbContext, userId);
        AccountRemovalRows.Seed(_dbContext, otherUserId);
        SeedProactiveData(userId);
        SeedProactiveData(otherUserId);
        SeedChatTurns(userId);
        SeedChatTurns(otherUserId);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        await AssertRetiredLedgerAbsent();

        await _service.RunAsync(CancellationToken.None);
        await AccountRemovalRows.AssertAsync(_dbContext, userId, 0, 0, true);
        await AccountRemovalRows.AssertAsync(_dbContext, otherUserId, 1, 1, true);

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

    [Fact]
    public async Task Erasure_WithGoogleRefreshToken_RevokesBeforeRowRemoval()
    {
        var userId = Guid.NewGuid();
        SeedDeactivatedUser(userId, "google-erasure@example.com", DateTime.UtcNow.AddDays(-1));
        var user = _dbContext.Users.Local.Single(u => u.Id == userId);
        user.SetGoogleTokens("access", "stored-refresh");
        user.Deactivate(DateTime.UtcNow.AddDays(-1));
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        _google.RevokeBestEffortAsync(userId, "stored-refresh", Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            (await _dbContext.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId)).GoogleRefreshToken.Should().Be("stored-refresh");
        });
        await _service.RunAsync(default);
        await _google.Received(1).RevokeBestEffortAsync(userId, "stored-refresh", Arg.Any<CancellationToken>());
        (await _dbContext.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == userId)).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Erasure_WithStripeCustomer_DeletesBeforeRemovalAndRetriesFailure(bool failFirst)
    {
        var userId = Guid.NewGuid();
        SeedDeactivatedUser(userId, "stripe-erasure@example.com", DateTime.UtcNow.AddDays(-1));
        var user = _dbContext.Users.Local.Single(u => u.Id == userId);
        user.SetStripeCustomerId("cus_erased");
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        var attempts = 0;
        _billing.DeleteCustomerAsync("cus_erased", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _dbContext.Users.IgnoreQueryFilters().Any(u => u.Id == userId).Should().BeTrue();
            if (++attempts == 1 && failFirst) throw new BillingProviderException("unavailable");
            return Task.CompletedTask;
        });
        await _service.RunAsync(default);
        if (failFirst)
        {
            (await _dbContext.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == userId)).Should().BeTrue();
            _dbContext.ChangeTracker.Clear();
            await _service.RunAsync(default);
        }
        attempts.Should().Be(failFirst ? 2 : 1);
        (await _dbContext.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == userId)).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Erasure_StripeDeletedThenLocalErasureFails_RestorationCreatesUsableCustomer(bool databaseFailure)
    {
        var userId = Guid.NewGuid();
        SeedDeactivatedUser(userId, "stripe-restoration@example.com", DateTime.UtcNow.AddDays(-1));
        _dbContext.Users.Local.Single(user => user.Id == userId).SetStripeCustomerId("cus_deleted");
        _dbContext.Tags.Add(Tag.Create(userId, "Retained after rollback", "#ff0000").Value);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        if (databaseFailure)
            await _dbContext.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_user_erasure BEFORE DELETE ON Users BEGIN SELECT RAISE(ABORT, 'erasure failure'); END;");
        else
            _objects.FailuresRemaining = 3;

        await _service.RunAsync(default);

        await _billing.Received(1).DeleteCustomerAsync("cus_deleted", Arg.Any<CancellationToken>());
        _objects.Attempts.Should().Be(databaseFailure ? 1 : 3);
        _dbContext.ChangeTracker.Clear();
        var retained = await _dbContext.Users.IgnoreQueryFilters().SingleAsync(user => user.Id == userId);
        retained.StripeCustomerId.Should().BeNull();
        retained.StripeCustomerGeneration.Should().Be(1);
        (await _dbContext.Tags.CountAsync(tag => tag.UserId == userId)).Should().Be(1);
        retained.CancelDeactivation();
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        _billing.CreateCustomerAsync(retained.Email, retained.Name, userId, Arg.Any<CancellationToken>(), 1)
            .Returns("cus_replacement");
        _billing.CreateCheckoutSessionAsync("cus_replacement", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            userId, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns("https://checkout.stripe.com/replacement");
        var prices = Substitute.For<IPriceResolver>();
        prices.Resolve("monthly", false).Returns("price_monthly");
        var checkout = new CreateCheckoutCommandHandler(new GenericRepository<User>(_dbContext),
            new UnitOfWork(_dbContext, new DatabaseConnectionSettings()), Substitute.For<IGeoLocationService>(),
            Options.Create(new StripeSettings()), _billing, prices, NullLogger<CreateCheckoutCommandHandler>.Instance);

        var result = await checkout.Handle(new(userId, "monthly", "US", null), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Url.Should().Be("https://checkout.stripe.com/replacement");
        await _billing.DidNotReceive().CreateCheckoutSessionAsync("cus_deleted", Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), userId, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        _dbContext.ChangeTracker.Clear();
        var restored = await _dbContext.Users.SingleAsync(user => user.Id == userId);
        restored.StripeCustomerId.Should().Be("cus_replacement");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Erasure_PendingCustomerSurvivesFailure_RecoveryPreservesRestoredBilling(bool acknowledgementFails)
    {
        var userId = Guid.NewGuid();
        SeedDeactivatedUser(userId, "pending-stripe@example.com", DateTime.UtcNow.AddDays(-1));
        _dbContext.Users.Local.Single(user => user.Id == userId).SetStripeCustomerId("cus_retired");
        _dbContext.Tags.Add(Tag.Create(userId, "Keep on restoration", "#ff0000").Value);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        if (acknowledgementFails)
            await _dbContext.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_deletion_ack BEFORE UPDATE OF StripeCustomerPendingDeletionId ON Users WHEN NEW.StripeCustomerPendingDeletionId IS NULL BEGIN SELECT RAISE(ABORT, 'ack failure'); END;");
        _billing.DeleteCustomerAsync("cus_retired", Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            _dbContext.Database.CurrentTransaction.Should().BeNull();
            var durable = await _dbContext.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(user => user.Id == userId);
            durable.StripeCustomerId.Should().BeNull();
            durable.StripeCustomerPendingDeletionId.Should().Be("cus_retired");
            if (!acknowledgementFails)
                throw new BillingProviderException("Provider response lost");
        });

        await _service.RunAsync(default);

        _dbContext.ChangeTracker.Clear();
        var retained = await _dbContext.Users.IgnoreQueryFilters().SingleAsync(user => user.Id == userId);
        retained.StripeCustomerPendingDeletionId.Should().Be("cus_retired");
        retained.StripeCustomerGeneration.Should().Be(1);
        retained.CancelDeactivation();
        retained.SetStripeCustomerId("cus_restored");
        retained.SetStripeSubscription("sub_restored", DateTime.UtcNow.AddDays(20));
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        if (acknowledgementFails)
            await _dbContext.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_deletion_ack;");
        _billing.DeleteCustomerAsync("cus_retired", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _service.RunAsync(default);

        await _billing.Received(2).DeleteCustomerAsync("cus_retired", Arg.Any<CancellationToken>());
        await _billing.DidNotReceive().DeleteCustomerAsync("cus_restored", Arg.Any<CancellationToken>());
        _dbContext.ChangeTracker.Clear();
        var restored = await _dbContext.Users.SingleAsync(user => user.Id == userId);
        restored.StripeCustomerPendingDeletionId.Should().BeNull();
        restored.StripeCustomerId.Should().Be("cus_restored");
        restored.StripeSubscriptionId.Should().Be("sub_restored");
        restored.IsPro.Should().BeTrue();
        _objects.Attempts.Should().Be(0);
        (await _dbContext.Tags.CountAsync(tag => tag.UserId == userId)).Should().Be(1);
    }

    [Fact]
    public async Task Erasure_CustomerDeletionIntentCannotCommit_DoesNotCallStripe()
    {
        var userId = Guid.NewGuid();
        SeedDeactivatedUser(userId, "stripe-intent@example.com", DateTime.UtcNow.AddDays(-1));
        _dbContext.Users.Local.Single(user => user.Id == userId).SetStripeCustomerId("cus_existing");
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        await _dbContext.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_deletion_intent BEFORE UPDATE OF StripeCustomerPendingDeletionId ON Users WHEN NEW.StripeCustomerPendingDeletionId IS NOT NULL BEGIN SELECT RAISE(ABORT, 'intent failure'); END;");

        await _service.RunAsync(default);

        await _billing.DidNotReceive().DeleteCustomerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _dbContext.ChangeTracker.Clear();
        var retained = await _dbContext.Users.IgnoreQueryFilters().SingleAsync(user => user.Id == userId);
        retained.StripeCustomerId.Should().Be("cus_existing");
        retained.StripeCustomerPendingDeletionId.Should().BeNull();
        retained.StripeCustomerGeneration.Should().Be(0);
        await _dbContext.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_deletion_intent;");

        await _service.RunAsync(default);

        await _billing.Received(1).DeleteCustomerAsync("cus_existing", Arg.Any<CancellationToken>());
        (await _dbContext.Users.IgnoreQueryFilters().AnyAsync(user => user.Id == userId)).Should().BeFalse();
    }

    [Fact]
    public async Task Erasure_ReactivatedDuringCustomerDeletion_PreservesNewCustomerAndAccount()
    {
        var userId = Guid.NewGuid();
        SeedDeactivatedUser(userId, "stripe-concurrent-restore@example.com", DateTime.UtcNow.AddDays(-1));
        _dbContext.Users.Local.Single(user => user.Id == userId).SetStripeCustomerId("cus_retired");
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        _billing.DeleteCustomerAsync("cus_retired", Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            var user = await _dbContext.Users.IgnoreQueryFilters().SingleAsync(user => user.Id == userId);
            user.CancelDeactivation();
            user.SetStripeCustomerId("cus_returning");
            await _dbContext.SaveChangesAsync();
        });

        await _service.RunAsync(default);

        _dbContext.ChangeTracker.Clear();
        var restored = await _dbContext.Users.SingleAsync(user => user.Id == userId);
        restored.StripeCustomerId.Should().Be("cus_returning");
        restored.StripeCustomerPendingDeletionId.Should().BeNull();
        _objects.Attempts.Should().Be(0);
        await _billing.DidNotReceive().DeleteCustomerAsync("cus_returning", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, 1, false)]
    [InlineData(true, 1, true)]
    [InlineData(true, 8, false)]
    public async Task Erasure_AppleTokenStillSet_RevokesOrDefers(bool fails, int daysOverdue, bool retained)
    {
        var userId = Guid.NewGuid();
        SeedDeactivatedUser(userId, "apple-erasure@example.com", DateTime.UtcNow.AddDays(-daysOverdue));
        _dbContext.Users.Local.Single(u => u.Id == userId).SetAppleIdentity("subject", "stored-apple", "client");
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        _apple.RevokeAsync("stored-apple", "client", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _dbContext.Users.IgnoreQueryFilters().Any(u => u.Id == userId).Should().BeTrue();
            return fails ? Task.FromException(new HttpRequestException("unavailable")) : Task.CompletedTask;
        });
        await _service.RunAsync(default);
        await _apple.Received(1).RevokeAsync("stored-apple", "client", Arg.Any<CancellationToken>());
        (await _dbContext.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == userId)).Should().Be(retained);
        if (fails && !retained)
            _logger.Received(1).Log(Microsoft.Extensions.Logging.LogLevel.Error,
                Arg.Is<Microsoft.Extensions.Logging.EventId>(eventId => eventId.Id == 11), Arg.Any<Arg.AnyType>(),
                Arg.Any<Exception?>(), Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(8, false)]
    public async Task Erasure_AppleProviderTimeout_RespectsDeferral(int daysOverdue, bool retained)
    {
        var userId = Guid.NewGuid();
        SeedDeactivatedUser(userId, "apple-timeout@example.com", DateTime.UtcNow.AddDays(-daysOverdue));
        _dbContext.Users.Local.Single(user => user.Id == userId).SetAppleIdentity("subject", "stored-apple", "client");
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        _apple.RevokeAsync("stored-apple", "client", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new TaskCanceledException("provider timeout")));
        var erase = () => _service.RunAsync(default);
        await erase.Should().NotThrowAsync();
        await _apple.Received(1).RevokeAsync("stored-apple", "client", Arg.Any<CancellationToken>());
        (await _dbContext.Users.IgnoreQueryFilters().AnyAsync(user => user.Id == userId)).Should().Be(retained);
        _logger.Received(1).Log(Microsoft.Extensions.Logging.LogLevel.Error,
            Arg.Is<Microsoft.Extensions.Logging.EventId>(eventId => eventId.Id == (retained ? 6 : 11)), Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception?>(), Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    [Fact]
    public async Task Erasure_AppleCallerCancellation_PropagatesAndKeepsRow()
    {
        using var cancellation = new CancellationTokenSource();
        var userId = Guid.NewGuid();
        SeedDeactivatedUser(userId, "apple-cancel@example.com", DateTime.UtcNow.AddDays(-1));
        _dbContext.Users.Local.Single(user => user.Id == userId).SetAppleIdentity("subject", "stored-apple", "client");
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        _apple.RevokeAsync("stored-apple", "client", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(cancellation.Token);
        });
        var erase = () => _service.RunAsync(cancellation.Token);
        await erase.Should().ThrowAsync<OperationCanceledException>();
        (await _dbContext.Users.IgnoreQueryFilters().AnyAsync(user => user.Id == userId)).Should().BeTrue();
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
