using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Exercises the real <see cref="PushNotificationService.SendToUserAsync"/> against an in-memory
/// SQLite <see cref="OrbitDbContext"/> and a stubbed HTTP transport. Web Push delivery is driven end
/// to end (valid VAPID + receiver EC keys are generated in-process so aes128gcm encryption reaches
/// the stubbed push service), which lets the dead-token-prune, transient-failure, and success paths
/// be asserted on real database state. FCM send is not reachable without Firebase credentials, so
/// only its "not initialized" guard is covered.
/// </summary>
public sealed class PushNotificationServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly OrbitDbContext _dbContext;
    private readonly VapidSettings _vapidSettings;
    private readonly string _receiverP256dh;
    private readonly string _receiverAuth;
    private readonly Guid _userId = Guid.NewGuid();

    public PushNotificationServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new SqliteCompatOrbitDbContext(options);
        _dbContext.Database.EnsureCreated();

        var user = User.Create("Push Tester", "push@useorbit.org").Value;
        typeof(User).GetProperty("Id")!.SetValue(user, _userId);
        _dbContext.Users.Add(user);
        _dbContext.SaveChanges();

        var (publicKey, privateKey) = GenerateVapidKeyPair();
        _vapidSettings = new VapidSettings
        {
            PublicKey = publicKey,
            PrivateKey = privateKey,
            Subject = "mailto:push-tests@useorbit.org"
        };

        (_receiverP256dh, _receiverAuth) = GenerateReceiverKeys();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private PushNotificationService CreateService(StubHttpMessageHandler handler) =>
        new(_dbContext, Options.Create(_vapidSettings), NullLogger<PushNotificationService>.Instance, new HttpClient(handler));

    private async Task<PushSubscription> SeedWebPushSubscription(string endpoint)
    {
        var sub = PushSubscription.Create(_userId, endpoint, _receiverP256dh, _receiverAuth).Value;
        _dbContext.PushSubscriptions.Add(sub);
        await _dbContext.SaveChangesAsync();
        return sub;
    }

    [Fact]
    public async Task SendToUserAsync_NoSubscriptions_DoesNothing()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var service = CreateService(handler);

        await service.SendToUserAsync(Guid.NewGuid(), "Title", "Body");

        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task SendToUserAsync_FcmSubscriptionAndFirebaseNotInitialized_KeepsSubscription()
    {
        var fcmSub = PushSubscription.Create(_userId, "fcm-device-token", PushSubscription.FcmSentinel, "auth").Value;
        _dbContext.PushSubscriptions.Add(fcmSub);
        await _dbContext.SaveChangesAsync();

        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var service = CreateService(handler);

        await service.SendToUserAsync(_userId, "Title", "Body");

        handler.CallCount.Should().Be(0);
        (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SendToUserAsync_WebPushDelivered_KeepsSubscription()
    {
        await SeedWebPushSubscription("https://push.example.com/sub/live");

        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var service = CreateService(handler);

        await service.SendToUserAsync(_userId, "Title", "Body", "/habits");

        handler.CallCount.Should().Be(1);
        (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task SendToUserAsync_WebPushSubscriptionDead_PrunesSubscription(HttpStatusCode deadStatus)
    {
        await SeedWebPushSubscription("https://push.example.com/sub/dead");

        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(deadStatus));
        var service = CreateService(handler);

        await service.SendToUserAsync(_userId, "Title", "Body");

        (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SendToUserAsync_WebPushTransientFailure_KeepsSubscriptionAndDoesNotThrow()
    {
        await SeedWebPushSubscription("https://push.example.com/sub/flaky");

        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var service = CreateService(handler);

        var act = () => service.SendToUserAsync(_userId, "Title", "Body");

        await act.Should().NotThrowAsync();
        (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SendToUserAsync_MixedLiveAndDeadWebPush_PrunesOnlyDeadSubscription()
    {
        const string liveEndpoint = "https://push.example.com/sub/keep";
        const string deadEndpoint = "https://push.example.com/sub/drop";
        await SeedWebPushSubscription(liveEndpoint);
        await SeedWebPushSubscription(deadEndpoint);

        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsoluteUri == deadEndpoint
                ? new HttpResponseMessage(HttpStatusCode.Gone)
                : new HttpResponseMessage(HttpStatusCode.Created));
        var service = CreateService(handler);

        await service.SendToUserAsync(_userId, "Title", "Body");

        var remaining = await _dbContext.PushSubscriptions.Select(s => s.Endpoint).ToListAsync();
        remaining.Should().ContainSingle().Which.Should().Be(liveEndpoint);
    }

    [Fact]
    public async Task SendToUserAsync_WebPushTransientThenSuccess_RetriesAndKeepsSubscription()
    {
        await SeedWebPushSubscription("https://push.example.com/sub/retry");

        var calls = 0;
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(calls++ == 0 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Created));
        var service = CreateService(handler);

        await service.SendToUserAsync(_userId, "Title", "Body");

        handler.CallCount.Should().Be(2);
        (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SendToUserAsync_WebPushTransportErrorThenSuccess_RetriesAndKeepsSubscription()
    {
        await SeedWebPushSubscription("https://push.example.com/sub/transport");

        var calls = 0;
        var handler = new StubHttpMessageHandler(_ => calls++ == 0
            ? throw new HttpRequestException("connection reset")
            : new HttpResponseMessage(HttpStatusCode.Created));
        var service = CreateService(handler);

        await service.SendToUserAsync(_userId, "Title", "Body");

        handler.CallCount.Should().Be(2);
        (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SendToUserAsync_WebPushSubscriptionGone_MarksStaleWithoutRetry()
    {
        await SeedWebPushSubscription("https://push.example.com/sub/gone");

        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Gone));
        var service = CreateService(handler);

        await service.SendToUserAsync(_userId, "Title", "Body");

        handler.CallCount.Should().Be(1);
        (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SendToUserAsync_WebPushNonTransientStatus_DoesNotRetryOrPruneSubscription()
    {
        await SeedWebPushSubscription("https://push.example.com/sub/rejected");

        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var service = CreateService(handler);

        await service.SendToUserAsync(_userId, "Title", "Body");

        handler.CallCount.Should().Be(1);
        (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(1);
    }

    private static (string PublicKey, string PrivateKey) GenerateVapidKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(true);
        return (Base64UrlEncode(UncompressedPoint(parameters)), Base64UrlEncode(LeftPad(parameters.D!, 32)));
    }

    private static (string P256dh, string Auth) GenerateReceiverKeys()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p256dh = Base64UrlEncode(UncompressedPoint(key.ExportParameters(false)));
        var auth = Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
        return (p256dh, auth);
    }

    private static byte[] UncompressedPoint(ECParameters parameters)
    {
        var buffer = new byte[65];
        buffer[0] = 0x04;
        LeftPad(parameters.Q.X!, 32).CopyTo(buffer, 1);
        LeftPad(parameters.Q.Y!, 32).CopyTo(buffer, 33);
        return buffer;
    }

    private static byte[] LeftPad(byte[] bytes, int size)
    {
        if (bytes.Length == size) return bytes;
        var padded = new byte[size];
        bytes.CopyTo(padded, size - bytes.Length);
        return padded;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responder(request));
        }
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
