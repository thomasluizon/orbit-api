using System.IO.Compression;
using System.Runtime;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Extensions;
using Orbit.Api.Observability;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Profile.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Events;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;
using Xunit.Abstractions;

namespace Orbit.Memory.Tests;

public sealed class MemoryLoadFactAttribute : FactAttribute
{
    public MemoryLoadFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ORBIT_MEMORY_GUARD") != "1")
            Skip = "Run in a fresh test host with ORBIT_MEMORY_GUARD=1; see infra/memory-budget.md.";
    }
}

public class ApiMemoryLoadTests(ITestOutputHelper output)
{
    private static readonly DateOnly Today = new(2026, 6, 1);

    [MemoryLoadFact]
    public async Task MixedLoad_SteadyAnonymousPlusShmemMemoryStaysBelowTheWarningLine()
    {
        Sample("start");
        output.WriteLine(JsonSerializer.Serialize(GC.GetConfigurationVariables()));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.AddOrbitDatabase();
        using var services = builder.Services.BuildServiceProvider();
        var memory = services.GetRequiredService<MemoryCache>();
        using var bus = new InMemoryAccountEventBus();
        var leases = new List<IDisposable>();
        try
        {
            for (var streamAccount = 0; streamAccount < 50; streamAccount++)
            {
                var userId = Guid.NewGuid();
                for (var connection = 0; connection < 3; connection++)
                {
                    bus.TrySubscribe(userId, Guid.NewGuid(), null, out var subscription).Should().BeTrue();
                    leases.Add(subscription!.Lease);
                }
                for (var change = 0; change < 300; change++)
                    bus.Publish(userId, new(1, [new Orbit.Domain.Events.AccountChange("habit", "update",
                        Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToArray())]));
            }
            Sample("event-streams-150");
            var estimator = new AiRequestCostEstimator(memory, Options.Create(new AiSettings
            {
                Pricing = new() { ["gpt-6-luna"] = new() { InputPerMillionUsd = 1, OutputPerMillionUsd = 1 } }
            }));
            await using var compressed = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AiReservation", "0.json.gz"));
            await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var captured = await JsonDocument.ParseAsync(gzip);
            var request = captured.RootElement.GetProperty("request").GetRawText();
            (await estimator.EstimateMicroDollarsAsync(request, default)).Should().BePositive();
            Sample("production-ai-request");
            using var factory = new SqliteOrbitDbContextFactory(Path.Combine(Path.GetTempPath(), $"orbit-memory-{Guid.NewGuid():N}.db"));
            var account = await SeedAccountAsync(factory);
            var encryption = new EncryptionService(Options.Create(new EncryptionSettings
            {
                Key = Convert.ToBase64String(new byte[32])
            }), NullLogger<EncryptionService>.Instance);
            var chat = new Orbit.Tests.Shared.ChatRankingWorkload(output, encryption);
            var samples = new List<long>();
            for (var round = 0; round < 20; round++)
            {
                await RunAccountAsync(factory, account.User, account.Habits, memory);
                await chat.RunAsync(20);
                (await estimator.EstimateMicroDollarsAsync(request, default)).Should().BePositive();
                var unreclaimable = Sample($"round-{round}");
                if (round > 0) samples.Add(unreclaimable);
            }
            var loadedManaged = GC.GetTotalMemory(false);
            var retainedManaged = GC.GetTotalMemory(forceFullCollection: true);
            Sample("retained-after-diagnostic-collection");
            output.WriteLine($"LoadedManagedBytes={loadedManaged}; RetainedManagedBytes={retainedManaged}; CacheEntries={memory.Count}");
            samples.Should().OnlyContain(bytes => bytes < ApiMemoryBudget.WarningBytes,
                $"steady anonymous plus shmem memory must stay below {ApiMemoryBudget.WarningBytes} bytes; samples: {string.Join(", ", samples)}");
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
        }
    }

    private static async Task<(User User, Habit[] Habits)> SeedAccountAsync(SqliteOrbitDbContextFactory factory)
    {
        var db = factory.Context;
        var user = User.Create("Memory workload", "memory@example.test").Value;
        user.SetTimeZone("America/Sao_Paulo");
        var habits = Enumerable.Range(0, 100).Select(index => Habit.Create(new HabitCreateParams(
            HabitSchedule.Recurring, user.Id, $"Habit {index}", FrequencyUnit.Day, 1, Today,
            Description: new string('x', 1000))).Value).ToArray();
        db.Users.Add(user);
        db.Habits.AddRange(habits);
        var instantAtUtc = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var index = 0; index < 10_000; index++)
            db.ConversationTurns.Add(ConversationTurn.Create(user.Id, Guid.NewGuid(), "user", instantAtUtc.AddSeconds(index),
                null, $"Conversation {index}: {new string('x', 256)}", "[]").Value);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (user, habits);
    }

    private static async Task RunAccountAsync(SqliteOrbitDbContextFactory factory, User user, Habit[] habits, MemoryCache memory)
    {
        using var db = factory.CreateContext();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(Today);
        dates.GetUserWeekStartDayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(1);
        dates.GetUserTimeZoneAsync(user.Id, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"));
        var repository = new GenericRepository<Habit>(db);
        var users = new GenericRepository<User>(db);
        var logs = new GenericRepository<HabitLog>(db);
        var unit = new UnitOfWork(db, new DatabaseConnectionSettings());
        var cache = new VersionedMemoryCache(memory, new UserCacheVersions(db));
        var reminders = new RepeatReminderReader(db, dates, TimeProvider.System);
        var schedule = new GetHabitScheduleQueryHandler(repository, new HabitScheduleLogReader(db),
            new HabitSchedulePageLoader(db), dates, unit, reminders);
        var mediator = Substitute.For<IMediator>();
        var summarySchedule = new GetDailySummaryScheduleQueryHandler(repository, new HabitScheduleLogReader(db),
            new HabitSchedulePageLoader(db), dates, unit, reminders);
        mediator.Send(Arg.Any<GetDailySummaryScheduleQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => summarySchedule.Handle(call.Arg<GetDailySummaryScheduleQuery>(), call.Arg<CancellationToken>()));
        var gate = Substitute.For<IPayGateService>();
        gate.CanUseDailySummary(user.Id, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var narrative = Substitute.For<ISummaryService>();
        narrative.GenerateSummaryAsync(Arg.Any<IEnumerable<Habit>>(), Arg.Any<DailySummaryContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DailySummaryContent("Your habits are ready for today.", string.Empty)));
        var summary = new GetDailySummaryQueryHandler(repository, users, logs, new HabitSummaryLogReader(db),
            gate, narrative, cache, dates, mediator);
        for (var action = 0; action < 4; action++)
        {
            (await schedule.Handle(new(user.Id, Today, Today, PageSize: 50), default)).Value.Items.Should().HaveCount(50);
            (await summary.Handle(new(user.Id, Today, Today, "en"), default)).IsSuccess.Should().BeTrue();
            var changes = new BulkUpdateHabitsCommandHandler(repository, dates, gate, unit, cache,
                NullLogger<BulkUpdateHabitsCommandHandler>.Instance);
            (await changes.Handle(new(user.Id, new(false, habits.Take(50).Select(h => h.Id).ToList()),
                new(HasDescription: true, Description: $"Bulk change {action}")), default)).Value.AppliedCount.Should().Be(50);
            db.ChangeTracker.Clear();
        }
        var progress = Substitute.For<IGoalProgressReadSyncer>();
        progress.ComputeFreshValuesAsync(user.Id, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, int>());
        var export = new StreamUserDataQueryHandler(new(users, repository, logs, new GenericRepository<Goal>(db),
            new GenericRepository<GoalProgressLog>(db), new GenericRepository<Tag>(db), new GenericRepository<Notification>(db),
            new GenericRepository<ChecklistTemplate>(db), new GenericRepository<UserAchievement>(db), new GenericRepository<StreakFreeze>(db),
            new GenericRepository<Referral>(db), new GenericRepository<ApiKey>(db), new GenericRepository<ProactiveItem>(db),
            new GenericRepository<NotionConnection>(db), new GenericRepository<NotionTask>(db),
            new GenericRepository<ConversationTurn>(db), new GenericRepository<ConversationOffer>(db)), dates, progress);
        var streamed = await export.Handle(new(user.Id), default);
        streamed.IsSuccess.Should().BeTrue();
        await streamed.Value.WriteAsync(Stream.Null, default);
    }

    private long Sample(string phase)
    {
        var snapshot = ProcessMemorySnapshot.Read();
        output.WriteLine(JsonSerializer.Serialize(new
        {
            phase,
            workingSet = snapshot.WorkingSetBytes,
            anonymous = snapshot.AnonymousBytes,
            shmem = snapshot.ShmemBytes,
            unreclaimable = snapshot.UnreclaimableBytes,
            managed = snapshot.ManagedBytes,
            committed = snapshot.CommittedBytes,
            heap = GC.GetGCMemoryInfo().HeapSizeBytes,
            available = snapshot.HeapLimitBytes,
            server = GCSettings.IsServerGC,
            processors = Environment.ProcessorCount
        }));
        return snapshot.UnreclaimableBytes;
    }
}
