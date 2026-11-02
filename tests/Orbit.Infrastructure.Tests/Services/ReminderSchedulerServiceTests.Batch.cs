using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public partial class ReminderSchedulerServiceTests
{
    [Theory]
    [InlineData(50)]
    [InlineData(300)]
    public async Task Tick_DueRemindersWithOneSent_SavesOnceAndBatchesDelivery(int count)
    {
        var saves = new ReminderSaveCounter();
        using var factory = new SqliteOrbitDbContextFactory(saves);
        var db = factory.Context;
        var user = User.Create("Alex", "batch@example.com").Value;
        db.Users.Add(user);
        var habits = Enumerable.Range(0, count).Select(i => Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            user.Id, $"Habit {i}", FrequencyUnit.Day, 1, UtcToday,
            DueTime: new TimeOnly(0, 0), ReminderEnabled: true, ReminderTimes: [0])).Value).ToArray();
        db.Habits.AddRange(habits);
        db.SentReminders.Add(SentReminder.Create(habits[0].Id, UtcToday, 0));
        await db.SaveChangesAsync();
        saves.Count = 0;
        var push = Substitute.For<IPushNotificationService>();
        await CreateService(db, push).CheckAndSendReminders(default);
        saves.Count.Should().Be(1);
        (await db.SentReminders.CountAsync()).Should().Be(count);
        await push.Received(1).SendManyAsync(Arg.Is<IReadOnlyList<PushDelivery>>(d => d.Count == count - 1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Tick_ReleasesGateBeforePushDelivery()
    {
        var db = CreateSqliteDbContext();
        var user = User.Create("Alex", "gate@example.com").Value;
        db.Users.Add(user);
        db.Habits.Add(Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Habit", FrequencyUnit.Day, 1,
            UtcToday, DueTime: new TimeOnly(0, 0), ReminderEnabled: true, ReminderTimes: [0])).Value);
        await db.SaveChangesAsync();
        var push = Substitute.For<IPushNotificationService>();
        var service = CreateService(db, push);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        push.SendManyAsync(Arg.Any<IReadOnlyList<PushDelivery>>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { entered.TrySetResult(); await release.Task; });
        var first = service.CheckAndSendReminders(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.CheckAndSendReminders(default).WaitAsync(TimeSpan.FromSeconds(5));
            first.IsCompleted.Should().BeFalse();
        }
        finally { release.TrySetResult(); await first; }
    }

    [Fact]
    public async Task Tick_OneUserChanges_DoesNotReloadAnotherUsersHabits()
    {
        var reads = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(reads);
        var db = factory.Context;
        var a = User.Create("A", "a@example.com").Value;
        var b = User.Create("B", "b@example.com").Value;
        db.Users.AddRange(a, b);
        var habits = new[] { a, b }.Select(u => Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            u.Id, u.Name, FrequencyUnit.Day, 1, UtcToday, DueTime: new TimeOnly(0, 0),
            ReminderEnabled: true, ReminderTimes: [0])).Value).ToArray();
        db.Habits.AddRange(habits);
        await db.SaveChangesAsync();
        var service = CreateService(db, Substitute.For<IPushNotificationService>());
        await service.CheckAndSendReminders(default);
        habits[0].SoftDelete();
        await db.SaveChangesAsync();
        reads.Reset();
        await service.CheckAndSendReminders(default);
        var snapshots = reads.Commands.Where(c => c.Sql.Contains("FROM \"Habits\"", StringComparison.Ordinal)
            && c.Sql.Contains("\"Title\"", StringComparison.Ordinal)).ToArray();
        snapshots.Should().NotBeEmpty();
        snapshots.Should().OnlyContain(c => !c.Parameters.Contains(b.Id.ToString()));
    }

    [Fact]
    public async Task Tick_RealPushService_DeliversLiveAndPrunesGone()
    {
        var db = CreateSqliteDbContext();
        var user = User.Create("Alex", "real-push@example.com").Value;
        db.Users.Add(user);
        db.Habits.Add(Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Habit", FrequencyUnit.Day, 1,
            UtcToday, DueTime: new TimeOnly(0, 0), ReminderEnabled: true, ReminderTimes: [0])).Value);
        using var senderKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        using var receiverKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var sender = senderKey.ExportParameters(true);
        var receiver = receiverKey.ExportParameters(false);
        var publicKey = new byte[] { 4 }.Concat(sender.Q.X!).Concat(sender.Q.Y!).ToArray();
        var receiverPublic = new byte[] { 4 }.Concat(receiver.Q.X!).Concat(receiver.Q.Y!).ToArray();
        var auth = Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        db.PushSubscriptions.AddRange(
            PushSubscription.Create(user.Id, "https://fcm.googleapis.com/sub/live", Encode(receiverPublic), auth).Value,
            PushSubscription.Create(user.Id, "https://fcm.googleapis.com/sub/gone", Encode(receiverPublic), auth).Value);
        await db.SaveChangesAsync();
        var handler = new SchedulerPushHandler();
        var push = new Orbit.Infrastructure.Services.PushNotificationService(db,
            Microsoft.Extensions.Options.Options.Create(new Orbit.Infrastructure.Configuration.VapidSettings
            {
                PublicKey = Encode(publicKey),
                PrivateKey = Encode(sender.D!),
                Subject = "mailto:test@example.com"
            }), Microsoft.Extensions.Logging.Abstractions.NullLogger<Orbit.Infrastructure.Services.PushNotificationService>.Instance,
            new HttpClient(handler), Substitute.For<IApnsPushSender>(), Substitute.For<IWnsPushSender>());
        await CreateService(db, push, forwardBatches: false).CheckAndSendReminders(default);
        handler.Urls.Should().Contain("https://fcm.googleapis.com/sub/live");
        (await db.PushSubscriptions.AsNoTracking().SingleAsync()).Endpoint.Should().EndWith("/live");
    }

    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private sealed class SchedulerPushHandler : HttpMessageHandler
    {
        internal System.Collections.Concurrent.ConcurrentBag<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Method.Should().Be(HttpMethod.Post);
            Urls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(request.RequestUri.AbsolutePath.EndsWith("gone", StringComparison.Ordinal)
                ? System.Net.HttpStatusCode.Gone : System.Net.HttpStatusCode.Created));
        }
    }

    private sealed class ReminderSaveCounter : SaveChangesInterceptor
    {
        internal int Count { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }
}
