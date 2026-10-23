using System.Diagnostics;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Common;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class ConversationTurnRecorderTests
{
    [Fact]
    public void TryRecord_FullQueue_DropsWithoutResolvingStorageOrBlocking()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        using var recorder = new ConversationTurnRecorder(scopeFactory, Options.Create(new AiSettings()),
            NullLogger<ConversationTurnRecorder>.Instance);
        var turn = Capture(Guid.NewGuid());
        for (var index = 0; index < ConversationTurnRecorder.Capacity; index++)
            recorder.TryRecord(turn with { TurnId = Guid.NewGuid() }).Should().BeTrue();
        var timer = Stopwatch.StartNew();
        recorder.TryRecord(turn).Should().BeFalse();
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        scopeFactory.DidNotReceive().CreateScope();
    }

    [Fact]
    public void Dispose_ThroughMultipleDiAliases_IsSafe()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => new ConversationTurnRecorder(Substitute.For<IServiceScopeFactory>(),
            Options.Create(new AiSettings()), NullLogger<ConversationTurnRecorder>.Instance));
        services.AddSingleton<IConversationTurnRecorder>(sp => sp.GetRequiredService<ConversationTurnRecorder>());
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => sp.GetRequiredService<ConversationTurnRecorder>());
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IConversationTurnRecorder>();
        provider.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
        Action dispose = provider.Dispose;
        dispose.Should().NotThrow();
    }

    [Fact]
    public async Task Consumer_MultipleBatches_UsesFreshContextsAndDrainsAllAcceptedTurns()
    {
        var contexts = new ContextTrackingInterceptor();
        using var fixture = new Fixture(interceptor: contexts);
        for (var index = 0; index < 33; index++)
            fixture.Recorder.TryRecord(Capture(fixture.User.Id)).Should().BeTrue();
        await fixture.Recorder.StartAsync(CancellationToken.None);
        await fixture.Recorder.StopAsync(CancellationToken.None);
        (await fixture.Database.Context.ConversationTurns.CountAsync()).Should().Be(66);
        contexts.ContextIds.Should().HaveCount(3).And.NotContain(fixture.Database.Context.ContextId.InstanceId);
    }

    [Fact]
    public async Task Consumer_DrainsOnShutdown_EncryptsContentAndArguments_AndReadsThemBack()
    {
        using var fixture = new Fixture();
        var capture = Capture(fixture.User.Id) with { ResponseJson = "{\"parts\":[{\"type\":\"text\",\"text\":\"Private composition\"}]}" };
        fixture.Recorder.TryRecord(capture).Should().BeTrue();
        await fixture.Recorder.StartAsync(CancellationToken.None);
        await fixture.Recorder.StopAsync(CancellationToken.None);
        fixture.Recorder.TryRecord(capture).Should().BeFalse();
        await using var read = fixture.Database.CreateContext();
        var rows = await read.ConversationTurns.Where(turn => turn.UserId == fixture.User.Id).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Single(turn => turn.Role == "assistant").Content.Should().Be(capture.AssistantContent);
        rows.Single(turn => turn.Role == "assistant").ToolCallsJson.Should().Be(capture.ToolCallsJson);
        rows.Single(turn => turn.Role == "assistant").ResponseJson.Should().Be(capture.ResponseJson);
        rows.Single(turn => turn.Role == "assistant").Model.Should().Be("recording-model");
        await using var command = read.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT \"Content\", \"ToolCallsJson\", \"ResponseJson\" FROM \"ConversationTurns\" WHERE \"Role\" = 'assistant'";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        var content = reader.GetString(0);
        var tools = reader.GetString(1);
        content.Should().StartWith("enc:").And.NotContain("Assistant private reply");
        tools.Should().StartWith("enc:").And.NotContain("Days can only be set");
        reader.GetString(2).Should().StartWith("enc:").And.NotContain("Private composition");
        var wrongKey = new EncryptionService(Options.Create(new EncryptionSettings { Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }),
            NullLogger<EncryptionService>.Instance);
        Action decrypt = () => wrongKey.Decrypt(content);
        decrypt.Should().Throw<CryptographicException>();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Consumer_DisabledFlagOrEncryption_DropsWithoutWriting(bool flagEnabled, bool encryptionConfigured)
    {
        using var fixture = new Fixture(flagEnabled: flagEnabled, encryptionConfigured: encryptionConfigured);
        fixture.Recorder.TryRecord(Capture(fixture.User.Id)).Should().BeTrue();
        await fixture.Recorder.StartAsync(CancellationToken.None);
        await fixture.Recorder.StopAsync(CancellationToken.None);
        (await fixture.Database.Context.ConversationTurns.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Consumer_ResetDeletesRowsAndRejectsPreviouslyQueuedTurns()
    {
        using var fixture = new Fixture();
        var capture = Capture(fixture.User.Id);
        fixture.Database.Context.ConversationTurns.Add(ConversationTurn.Create(fixture.User.Id, Guid.NewGuid(),
            "user", capture.CreatedAtUtc, null, "Persisted", "[]").Value);
        await fixture.Database.Context.SaveChangesAsync();
        fixture.Recorder.TryRecord(capture).Should().BeTrue();
        var reset = new AccountResetRepository(fixture.Database.Context, Substitute.For<Orbit.Domain.Events.IAccountEventCollector>());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(fixture.User.Id, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 6, 15));
        var handler = new ResetAccountCommandHandler(new GenericRepository<User>(fixture.Database.Context), reset,
            new UnitOfWork(fixture.Database.Context, new DatabaseConnectionSettings()), dates, cache);
        var result = await handler.Handle(new ResetAccountCommand(fixture.User.Id), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        (await fixture.Database.Context.ConversationTurns.CountAsync()).Should().Be(0);
        var afterReset = Capture(fixture.User.Id) with
        {
            CreatedAtUtc = fixture.User.ConversationHistoryClearedAtUtc!.Value.AddTicks(1)
        };
        fixture.Recorder.TryRecord(afterReset).Should().BeTrue();
        await fixture.Recorder.StartAsync(CancellationToken.None);
        await fixture.Recorder.StopAsync(CancellationToken.None);
        var rows = await fixture.Database.Context.ConversationTurns.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2).And.OnlyContain(turn => turn.TurnId == afterReset.TurnId);
    }

    [Fact]
    public async Task Consumer_DeletedAccount_DoesNotRecreateQueuedContent()
    {
        using var fixture = new Fixture();
        fixture.Recorder.TryRecord(Capture(fixture.User.Id)).Should().BeTrue();
        await fixture.Database.Context.Users.Where(user => user.Id == fixture.User.Id).ExecuteDeleteAsync();
        await fixture.Recorder.StartAsync(CancellationToken.None);
        await fixture.Recorder.StopAsync(CancellationToken.None);
        (await fixture.Database.Context.ConversationTurns.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Consumer_PersistenceException_DropsFailedBatchAndContinues()
    {
        var failure = new FailingSaveInterceptor();
        using var fixture = new Fixture(interceptor: failure);
        failure.FailNext = true;
        fixture.Recorder.TryRecord(Capture(fixture.User.Id)).Should().BeTrue();
        await fixture.Recorder.StartAsync(CancellationToken.None);
        await failure.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var next = Capture(fixture.User.Id);
        fixture.Recorder.TryRecord(next).Should().BeTrue();
        await fixture.Recorder.StopAsync(CancellationToken.None);
        var rows = await fixture.Database.Context.ConversationTurns.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2).And.OnlyContain(turn => turn.TurnId == next.TurnId);
    }

    [Fact]
    public async Task Consumer_BlockedWrite_ShutdownHonorsDeadline()
    {
        var blocker = new BlockingSaveInterceptor();
        using var fixture = new Fixture(interceptor: blocker);
        blocker.Block = true;
        fixture.Recorder.TryRecord(Capture(fixture.User.Id)).Should().BeTrue();
        await fixture.Recorder.StartAsync(CancellationToken.None);
        await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await fixture.Recorder.StopAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(2));
        await blocker.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static ConversationTurnCapture Capture(Guid userId) => new(userId, Guid.NewGuid(), DateTime.UtcNow,
        "User private message", "Assistant private reply",
        """[{"CallId":"one","Name":"create_habit","Arguments":{"days":["Monday"]},"Success":false,"Error":"Days can only be set when frequency quantity is 1"}]""", true);

    private sealed class Fixture : IDisposable
    {
        public SqliteOrbitDbContextFactory Database { get; }
        public User User { get; }
        public ConversationTurnRecorder Recorder { get; }
        private readonly ServiceProvider _provider;

        public Fixture(bool flagEnabled = true, bool encryptionConfigured = true, IInterceptor? interceptor = null)
        {
            var encryption = new EncryptionService(Options.Create(new EncryptionSettings
            {
                Key = encryptionConfigured ? Convert.ToBase64String(new byte[32]) : string.Empty
            }), NullLogger<EncryptionService>.Instance);
            Database = new SqliteOrbitDbContextFactory(encryption, interceptor is null ? [] : [interceptor]);
            User = User.Create("Person", "recording@example.com").Value;
            Database.Context.Users.Add(User);
            Database.Context.AppFeatureFlags.Single(flag => flag.Key == FeatureFlagKeys.AstraConversationRecording).SetEnabled(flagEnabled);
            Database.Context.SaveChanges();
            var services = new ServiceCollection();
            services.AddScoped(_ => Database.CreateContext());
            services.AddSingleton<IEncryptionService>(encryption);
            services.AddScoped<IUnitOfWork>(sp => new UnitOfWork(sp.GetRequiredService<OrbitDbContext>(), new DatabaseConnectionSettings()));
            services.AddMemoryCache();
            services.AddScoped<IFeatureFlagService, FeatureFlagService>();
            _provider = services.BuildServiceProvider();
            Recorder = new ConversationTurnRecorder(_provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new AiSettings { Model = "recording-model" }), NullLogger<ConversationTurnRecorder>.Instance);
        }

        public void Dispose()
        {
            Recorder.Dispose();
            _provider.Dispose();
            Database.Dispose();
        }
    }

    private sealed class ContextTrackingInterceptor : SaveChangesInterceptor
    {
        public HashSet<Guid> ContextIds { get; } = [];
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            ContextIds.Add(eventData.Context!.ContextId.InstanceId);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public bool FailNext { get; set; }
        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (FailNext)
            {
                FailNext = false;
                Attempted.TrySetResult();
                throw new InvalidOperationException("Forced persistence failure");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BlockingSaveInterceptor : SaveChangesInterceptor
    {
        public bool Block { get; set; }
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Block)
            {
                Entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { CancellationObserved.TrySetResult(); throw; }
            }
            return result;
        }
    }
}
