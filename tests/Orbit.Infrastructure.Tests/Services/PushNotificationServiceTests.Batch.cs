using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class PushNotificationServiceTests
{
    [Theory]
    [InlineData(30, true)]
    [InlineData(501, false)]
    public async Task SendMany_OneQuery_ChunksFcmAndPrunesOnlyGone(int count, bool separateUsers)
    {
        var commands = new PushCommands();
        using var factory = new SqliteOrbitDbContextFactory(commands);
        var db = factory.Context;
        var users = Enumerable.Range(0, separateUsers ? count : 1).Select(i => User.Create($"User {i}", $"user{i}@example.com").Value).ToArray();
        db.Users.AddRange(users);
        for (var i = 0; i < count; i++)
            db.PushSubscriptions.Add(PushSubscription.Create(users[separateUsers ? i : 0].Id, $"device-{i:D4}", "fcm", "auth").Value);
        await db.SaveChangesAsync();
        var sent = new ConcurrentBag<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var token = body.RootElement.GetProperty("message").GetProperty("token").GetString()!;
            sent.Add(token);
            return token == $"device-{count - 1:D4}" ? FcmError("UNREGISTERED") : FcmSuccess();
        });
        var app = CreateFirebase(handler);
        try
        {
            commands.Reads = commands.Deletes = 0;
            var service = new ObservingPushService(db, _vapidSettings, new HttpClient());
            await service.SendManyAsync(users.Select(u => new PushDelivery(u.Id, "Title", "Body")).ToArray());
            service.Chunks.Should().Equal(count == 501 ? [500, 1] : [30]);
            commands.Reads.Should().Be(1);
            commands.Deletes.Should().Be(1);
            sent.Should().HaveCount(count).And.OnlyHaveUniqueItems();
            (await db.PushSubscriptions.CountAsync()).Should().Be(count - 1);
        }
        finally { app.Delete(); }
    }

    [Fact]
    public async Task Fcm_BatchThrows_FallsBackOncePerSubscription()
    {
        _dbContext.PushSubscriptions.AddRange(PushSubscription.Create(_userId, "one", "fcm", "auth").Value,
            PushSubscription.Create(_userId, "two", "fcm", "auth").Value);
        await _dbContext.SaveChangesAsync();
        var handler = new StubHttpMessageHandler(_ => FcmSuccess());
        var app = CreateFirebase(handler);
        try
        {
            var service = new ObservingPushService(_dbContext, _vapidSettings, new HttpClient()) { FailBatch = true };
            (await service.TrySendToUserAsync(_userId, "Title", "Body")).Should().BeTrue();
            handler.CallCount.Should().Be(2);
            service.Chunks.Should().Equal(2);
            (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(2);
        }
        finally { app.Delete(); }
    }

    [Fact]
    public async Task WebPush_OneEndpointNeverAnswers_TwoOthersDeliverWithinOneTimeout()
    {
        await SeedWebPushSubscription("https://fcm.googleapis.com/sub/hangs");
        await SeedWebPushSubscription("https://fcm.googleapis.com/sub/live-one");
        await SeedWebPushSubscription("https://fcm.googleapis.com/sub/live-two");
        var handler = new HangingPushHandler();
        var service = new PushNotificationService(_dbContext, Options.Create(_vapidSettings), NullLogger<PushNotificationService>.Instance,
            new HttpClient(handler), Substitute.For<IApnsPushSender>(), Substitute.For<IWnsPushSender>(),
            Options.Create(new PushDeliverySettings { AttemptTimeoutSeconds = 1, Concurrency = 3 }));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        (await service.TrySendToUserAsync(_userId, "Title", "Body")).Should().BeTrue();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        handler.Calls.Should().Be(3);
        (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apns_TransportException_RetainsSubscription(bool timeout)
    {
        _dbContext.PushSubscriptions.Add(PushSubscription.Create(_userId, "device", "apns", "auth").Value);
        await _dbContext.SaveChangesAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var handler = new StubHttpMessageHandler(_ => throw (timeout
            ? new TaskCanceledException("provider timeout") : new HttpRequestException("connection reset")));
        using var sender = new ApnsPushSender(new HttpClient(handler), Options.Create(new ApnsSettings
        {
            KeyId = "KEY",
            TeamId = "TEAM",
            Topic = "org.useorbit.app",
            PrivateKey = key.ExportPkcs8PrivateKeyPem()
        }), NullLogger<ApnsPushSender>.Instance, TimeProvider.System);
        var service = new PushNotificationService(_dbContext, Options.Create(_vapidSettings), NullLogger<PushNotificationService>.Instance,
            new HttpClient(), sender, Substitute.For<IWnsPushSender>());
        (await service.TrySendToUserAsync(_userId, "Title", "Body")).Should().BeFalse();
        (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task StaleSubscriptions_OneDelete_PreservesRefreshedRegistration()
    {
        var commands = new PushCommands();
        using var factory = new SqliteOrbitDbContextFactory(commands);
        var db = factory.Context;
        var user = User.Create("User", "prune@example.com").Value;
        db.Users.Add(user);
        var rows = Enumerable.Range(0, 3).Select(i => PushSubscription.Create(user.Id,
            $"https://fcm.googleapis.com/sub/stale-{i}", _receiverP256dh, _receiverAuth).Value).ToArray();
        db.PushSubscriptions.AddRange(rows);
        await db.SaveChangesAsync();
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("stale-0", StringComparison.Ordinal))
            {
                using var refresh = factory.CreateContext();
                var current = refresh.PushSubscriptions.Single(s => s.Id == rows[0].Id);
                refresh.Entry(current).Property(s => s.Auth).CurrentValue = "new-auth";
                refresh.SaveChanges();
            }
            return new HttpResponseMessage(HttpStatusCode.Gone);
        });
        var service = new PushNotificationService(db, Options.Create(_vapidSettings), NullLogger<PushNotificationService>.Instance,
            new HttpClient(handler), Substitute.For<IApnsPushSender>(), Substitute.For<IWnsPushSender>());
        commands.Deletes = 0;
        (await service.TrySendToUserAsync(user.Id, "Title", "Body")).Should().BeFalse();
        commands.Deletes.Should().Be(1);
        var kept = await db.PushSubscriptions.AsNoTracking().SingleAsync();
        kept.Id.Should().Be(rows[0].Id);
        kept.Auth.Should().Be("new-auth");
        foreach (var column in new[] { "Id", "UserId", "Endpoint", "P256dh", "Auth", "ApnsTopic", "DeviceId", "CreatedAtUtc" })
            commands.DeleteSql.Should().Contain($"\"{column}\"");
    }

    [Fact]
    public async Task FcmHttpHandler_ConcurrencyOne_SerializesSdkRequests()
    {
        var wire = new ConcurrentPushHandler();
        using var http = new FcmDeliveryHttpClientFactory(new PushDeliverySettings { Concurrency = 1 }, wire)
            .CreateHttpClient(new Google.Apis.Http.CreateHttpClientArgs());
        http.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
        var responses = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => http.GetAsync("https://fcm.googleapis.com/test")));
        foreach (var response in responses) response.Dispose();
        wire.Peak.Should().Be(1);
        wire.Calls.Should().Be(30);
    }

    [Fact]
    public async Task Fcm_FullSdkBatch_QueuedRecipientsGetTheirOwnAttemptBudget()
    {
        for (var i = 0; i < 500; i++)
            _dbContext.PushSubscriptions.Add(PushSubscription.Create(_userId, $"token-{i}", "fcm", "auth").Value);
        await _dbContext.SaveChangesAsync();
        var wire = new ConcurrentPushHandler { Latency = TimeSpan.FromMilliseconds(30) };
        var settings = new PushDeliverySettings { Concurrency = 8, AttemptTimeoutSeconds = 1 };
        var app = FirebaseApp.Create(new AppOptions
        {
            Credential = GoogleCredential.FromAccessToken("test-access-token"),
            ProjectId = "test-project",
            HttpClientFactory = new FcmDeliveryHttpClientFactory(settings, wire)
        });
        try
        {
            using var service = new PushNotificationService(_dbContext, Options.Create(_vapidSettings), NullLogger<PushNotificationService>.Instance,
                new HttpClient(), Substitute.For<IApnsPushSender>(), Substitute.For<IWnsPushSender>(), Options.Create(settings));
            (await service.TrySendToUserAsync(_userId, "Title", "Body")).Should().BeTrue();
            wire.Calls.Should().Be(500);
            wire.Completed.Should().Be(500);
            wire.Peak.Should().BeLessThanOrEqualTo(8);
            (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(500);
        }
        finally { app.Delete(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fcm_BoundedSdkRequests_TimeoutKeepsHealthyRecipientAndCallerCancellationPropagates(bool cancelCaller)
    {
        _dbContext.PushSubscriptions.AddRange(
            PushSubscription.Create(_userId, "hang", "fcm", "auth").Value,
            PushSubscription.Create(_userId, "live", "fcm", "auth").Value);
        await _dbContext.SaveChangesAsync();
        var wire = new BlockingFcmHandler();
        var settings = new PushDeliverySettings { Concurrency = 1, AttemptTimeoutSeconds = 1 };
        var app = FirebaseApp.Create(new AppOptions
        {
            Credential = GoogleCredential.FromAccessToken("test-access-token"),
            ProjectId = "test-project",
            HttpClientFactory = new FcmDeliveryHttpClientFactory(settings, wire)
        });
        try
        {
            using var cancellation = new CancellationTokenSource();
            using var service = new PushNotificationService(_dbContext, Options.Create(_vapidSettings), NullLogger<PushNotificationService>.Instance,
                new HttpClient(), Substitute.For<IApnsPushSender>(), Substitute.For<IWnsPushSender>(), Options.Create(settings));
            var send = service.TrySendToUserAsync(_userId, "Title", "Body", cancellationToken: cancellation.Token);
            await wire.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancelCaller)
            {
                cancellation.Cancel();
                var observe = async () => await send;
                await observe.Should().ThrowAsync<OperationCanceledException>();
            }
            else
            {
                (await send).Should().BeTrue();
                wire.LiveDeliveries.Should().Be(1);
            }
            wire.HangingAttempts.Should().Be(1);
            (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(2);
        }
        finally { app.Delete(); }
    }

    [Fact]
    public async Task Fcm_ResponseBodyStalls_ActiveAttemptDeadlineStillApplies()
    {
        _dbContext.PushSubscriptions.Add(PushSubscription.Create(_userId, "body-stalls", "fcm", "auth").Value);
        await _dbContext.SaveChangesAsync();
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new SlowFcmContent()
        });
        var settings = new PushDeliverySettings { Concurrency = 1, AttemptTimeoutSeconds = 1 };
        var app = FirebaseApp.Create(new AppOptions
        {
            Credential = GoogleCredential.FromAccessToken("test-access-token"),
            ProjectId = "test-project",
            HttpClientFactory = new FcmDeliveryHttpClientFactory(settings, handler)
        });
        try
        {
            using var service = new PushNotificationService(_dbContext, Options.Create(_vapidSettings), NullLogger<PushNotificationService>.Instance,
                new HttpClient(), Substitute.For<IApnsPushSender>(), Substitute.For<IWnsPushSender>(), Options.Create(settings));
            (await service.TrySendToUserAsync(_userId, "Title", "Body")).Should().BeFalse();
            handler.CallCount.Should().Be(1);
            (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(1);
        }
        finally { app.Delete(); }
    }

    [Fact]
    public async Task Fcm_FallbackQueuedBehindAnotherBatch_GetsItsOwnActiveAttemptBudget()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var batchUser = User.Create("Batch", "batch-budget@example.com").Value;
        var fallbackUser = User.Create("Fallback", "fallback-budget@example.com").Value;
        db.Users.AddRange(batchUser, fallbackUser);
        for (var i = 0; i < 5; i++)
            db.PushSubscriptions.Add(PushSubscription.Create(batchUser.Id, $"batch-{i}", "fcm", "auth").Value);
        db.PushSubscriptions.Add(PushSubscription.Create(fallbackUser.Id, "fallback", "fcm", "auth").Value);
        await db.SaveChangesAsync();
        using var fallbackDb = factory.CreateContext();
        var wire = new ConcurrentPushHandler { Latency = TimeSpan.FromMilliseconds(300) };
        var settings = new PushDeliverySettings { Concurrency = 1, AttemptTimeoutSeconds = 1 };
        var app = FirebaseApp.Create(new AppOptions
        {
            Credential = GoogleCredential.FromAccessToken("test-access-token"),
            ProjectId = "test-project",
            HttpClientFactory = new FcmDeliveryHttpClientFactory(settings, wire)
        });
        try
        {
            using var normal = new ObservingPushService(db, _vapidSettings, new HttpClient(), settings);
            using var fallback = new ObservingPushService(fallbackDb, _vapidSettings, new HttpClient(), settings) { FailBatch = true };
            var batch = normal.TrySendToUserAsync(batchUser.Id, "Title", "Body");
            await wire.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var retried = fallback.TrySendToUserAsync(fallbackUser.Id, "Title", "Body");
            (await batch).Should().BeTrue();
            (await retried).Should().BeTrue();
            wire.Calls.Should().Be(6);
            wire.Completed.Should().Be(6);
            wire.Peak.Should().Be(1);
        }
        finally { app.Delete(); }
    }

    private sealed class SlowFcmContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("""{"name":"projects/test-project/messages/one"}"""), cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class BlockingFcmHandler : HttpMessageHandler
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int HangingAttempts;
        internal int LiveDeliveries;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (body.RootElement.GetProperty("message").GetProperty("token").GetString() == "hang")
            {
                Interlocked.Increment(ref HangingAttempts);
                Started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            Interlocked.Increment(ref LiveDeliveries);
            return FcmSuccess();
        }
    }

    private sealed class ConcurrentPushHandler : HttpMessageHandler
    {
        internal int Peak;
        internal int Calls;
        internal int Completed;
        internal TimeSpan Latency = TimeSpan.FromMilliseconds(10);
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            Peak = Math.Max(Peak, active);
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            try
            {
                await Task.Delay(Latency, cancellationToken);
                Interlocked.Increment(ref Completed);
                return FcmSuccess();
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private static FirebaseApp CreateFirebase(HttpMessageHandler handler) => FirebaseApp.Create(new AppOptions
    {
        Credential = GoogleCredential.FromAccessToken("test-access-token"),
        ProjectId = "test-project",
        HttpClientFactory = new FcmHttpClientFactory(handler)
    });
    private static HttpResponseMessage FcmSuccess() => new(HttpStatusCode.OK)
    { Content = new StringContent("""{"name":"projects/test-project/messages/one"}""") };
    private static HttpResponseMessage FcmError(string code) => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            error = new
            {
                status = "INVALID_ARGUMENT",
                message = "failed",
                details = new[] {
                new Dictionary<string, string> { ["@type"] = "type.googleapis.com/google.firebase.fcm.v1.FcmError", ["errorCode"] = code } }
            }
        }))
    };

    private sealed class ObservingPushService(OrbitDbContext db, VapidSettings vapid, HttpClient http, PushDeliverySettings? delivery = null)
        : PushNotificationService(db, Options.Create(vapid), NullLogger<PushNotificationService>.Instance, http,
            Substitute.For<IApnsPushSender>(), Substitute.For<IWnsPushSender>(), Options.Create(delivery ?? new()))
    {
        internal List<int> Chunks { get; } = [];
        internal bool FailBatch { get; init; }
        internal override Task<BatchResponse> SendFcmBatchAsync(IReadOnlyList<Message> messages, CancellationToken ct)
        {
            Chunks.Add(messages.Count);
            return FailBatch ? Task.FromException<BatchResponse>(new InvalidOperationException("batch failed"))
                : base.SendFcmBatchAsync(messages, ct);
        }
    }
    private sealed class HangingPushHandler : HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (request.RequestUri!.AbsolutePath.EndsWith("hangs", StringComparison.Ordinal))
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }
    private sealed class PushCommands : DbCommandInterceptor
    {
        internal int Reads;
        internal int Deletes;
        internal string DeleteSql = "";
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"PushSubscriptions\"", StringComparison.Ordinal)) Reads++;
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("DELETE", StringComparison.Ordinal)) { Deletes++; DeleteSql = command.CommandText; }
            return ValueTask.FromResult(result);
        }
    }
}
