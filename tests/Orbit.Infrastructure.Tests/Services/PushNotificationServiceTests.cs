using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FirebaseAdmin.Messaging;
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

        var delivered = await service.TrySendToUserAsync(Guid.NewGuid(), "Title", "Body");

        delivered.Should().BeFalse();
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

        var delivered = await service.TrySendToUserAsync(_userId, "Title", "Body");

        delivered.Should().BeFalse();
        handler.CallCount.Should().Be(0);
        (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("https://example.com/push")]
    [InlineData("https://fcm.googleapis.com.example.com/push")]
    [InlineData("https://web.push.apple.com.example.com/push")]
    [InlineData("https://cloud.notify.windows.com.example.com/push")]
    [InlineData("https://127.0.0.1/push")]
    [InlineData("http://fcm.googleapis.com/push")]
    public async Task SendToUserAsync_UnsupportedStoredEndpoint_SkipsDeliveryAndKeepsSubscription(string endpoint)
    {
        var subscription = await SeedWebPushSubscription("https://fcm.googleapis.com/fcm/send/legacy");
        _dbContext.Entry(subscription).Property(s => s.Endpoint).CurrentValue = endpoint;
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var service = CreateService(handler);

        var delivered = await service.TrySendToUserAsync(_userId, "Title", "Body");

        delivered.Should().BeFalse();
        handler.CallCount.Should().Be(0);
        (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(1);
    }

    /// <summary>
    /// Sources: https://web.dev/articles/codelab-notifications-push-server,
    /// https://mozilla-services.github.io/autopush-rs/http.html,
    /// https://github.com/andreinwald/webpush-ios-example#subscription-and-saving-token, and
    /// WNS: https://learn.microsoft.com/en-us/windows/apps/develop/notifications/push-notifications/wns-overview; Edge: https://learn.microsoft.com/en-us/answers/questions/5593523/why-notifications-fail-in-azure-notification-hub-a.
    /// </summary>
    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/cpqAgzGzkzQ:APA9...")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v1/...")]
    [InlineData("https://web.push.apple.com/QGuQyavXutnMH...")]
    [InlineData("https://cloud.notify.windows.com/?token=AQE%bU%2fSjZOCvRjjpILow%3d%3d")]
    [InlineData("https://wns2-sg2p.notify.windows.com/w/?token=...")]
    public async Task SendToUserAsync_WebPushDelivered_KeepsSubscription(string endpoint)
    {
        await SeedWebPushSubscription(endpoint);

        var handler = new StubHttpMessageHandler(request =>
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri.Should().Be(new Uri(endpoint));
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        var service = CreateService(handler);

        var delivered = await service.TrySendToUserAsync(_userId, "Title", "Body", "/habits");

        delivered.Should().BeTrue();
        handler.CallCount.Should().Be(1);
        (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SendToUserAsync_MixedSupportedAndUnsupportedStoredEndpoints_SendsOnlyToProvider()
    {
        const string supportedEndpoint = "https://fcm.googleapis.com/fcm/send/live";
        var legacy = await SeedWebPushSubscription("https://fcm.googleapis.com/fcm/send/legacy");
        _dbContext.Entry(legacy).Property(s => s.Endpoint).CurrentValue = "https://example.com/push";
        await _dbContext.SaveChangesAsync();
        await SeedWebPushSubscription(supportedEndpoint);
        _dbContext.ChangeTracker.Clear();
        var destinations = new List<Uri>();
        var handler = new StubHttpMessageHandler(request =>
        {
            destinations.Add(request.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.Created);
        });

        var delivered = await CreateService(handler).TrySendToUserAsync(_userId, "Title", "Body");

        delivered.Should().BeTrue();
        destinations.Should().ContainSingle().Which.Should().Be(new Uri(supportedEndpoint));
        (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task SendToUserAsync_RedirectResponse_DoesNotRetryOrReportDelivery(HttpStatusCode status)
    {
        await SeedWebPushSubscription("https://fcm.googleapis.com/fcm/send/redirect");
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(status)
        {
            Headers = { Location = new Uri("https://example.com/redirected") }
        });

        var delivered = await CreateService(handler).TrySendToUserAsync(_userId, "Title", "Body");

        delivered.Should().BeFalse();
        handler.CallCount.Should().Be(1);
        (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task SendToUserAsync_WebPushSubscriptionDead_PrunesSubscription(HttpStatusCode deadStatus)
    {
        await SeedWebPushSubscription("https://fcm.googleapis.com/sub/dead");

        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(deadStatus));
        var service = CreateService(handler);

        var delivered = await service.TrySendToUserAsync(_userId, "Title", "Body");

        delivered.Should().BeFalse();
        (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SendToUserAsync_WebPushTransientFailure_KeepsSubscriptionAndDoesNotThrow()
    {
        await SeedWebPushSubscription("https://fcm.googleapis.com/sub/flaky");

        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var service = CreateService(handler);

        var delivered = await service.TrySendToUserAsync(_userId, "Title", "Body");

        delivered.Should().BeFalse();
        handler.CallCount.Should().Be(3);
        (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SendToUserAsync_MixedLiveAndDeadWebPush_PrunesOnlyDeadSubscription()
    {
        const string liveEndpoint = "https://fcm.googleapis.com/sub/keep";
        const string deadEndpoint = "https://fcm.googleapis.com/sub/drop";
        await SeedWebPushSubscription(liveEndpoint);
        await SeedWebPushSubscription(deadEndpoint);

        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsoluteUri == deadEndpoint
                ? new HttpResponseMessage(HttpStatusCode.Gone)
                : new HttpResponseMessage(HttpStatusCode.Created));
        var service = CreateService(handler);

        var delivered = await service.TrySendToUserAsync(_userId, "Title", "Body");

        delivered.Should().BeTrue();
        var remaining = await _dbContext.PushSubscriptions.Select(s => s.Endpoint).ToListAsync();
        remaining.Should().ContainSingle().Which.Should().Be(liveEndpoint);
    }

    [Fact]
    public async Task SendToUserAsync_WebPushTransientThenSuccess_RetriesAndKeepsSubscription()
    {
        await SeedWebPushSubscription("https://fcm.googleapis.com/sub/retry");

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
        await SeedWebPushSubscription("https://fcm.googleapis.com/sub/transport");

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
        await SeedWebPushSubscription("https://fcm.googleapis.com/sub/gone");

        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Gone));
        var service = CreateService(handler);

        await service.SendToUserAsync(_userId, "Title", "Body");

        handler.CallCount.Should().Be(1);
        (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SendToUserAsync_WebPushNonTransientStatus_DoesNotRetryOrPruneSubscription()
    {
        await SeedWebPushSubscription("https://fcm.googleapis.com/sub/rejected");

        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var service = CreateService(handler);

        await service.SendToUserAsync(_userId, "Title", "Body");

        handler.CallCount.Should().Be(1);
        (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SendToUserAsync_HostileOverlongContent_DeliversPayloadUnderPlatformLimit()
    {
        await SeedWebPushSubscription("https://fcm.googleapis.com/sub/hostile");

        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var service = CreateService(handler);

        var hostileTitle = "Alert" + (char)0 + (char)27 + (char)127 + new string('A', 5_000);
        var hostileBody = "Body" + (char)10 + (char)13 + (char)9 + new string('B', 20_000);

        await service.SendToUserAsync(_userId, hostileTitle, hostileBody, "/habits");

        handler.CallCount.Should().Be(1);
        handler.LastRequestContentLength.Should().BeLessThanOrEqualTo(4096);
        (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(MessagingErrorCode.QuotaExceeded)]
    [InlineData(MessagingErrorCode.ThirdPartyAuthError)]
    [InlineData(MessagingErrorCode.Internal)]
    [InlineData(MessagingErrorCode.Unavailable)]
    [InlineData(null)]
    public void IsStaleFcmError_RateLimitAuthAndTransientErrors_KeepsSubscription(MessagingErrorCode? code) =>
        PushNotificationService.IsStaleFcmError(code).Should().BeFalse();

    [Theory]
    [InlineData(MessagingErrorCode.Unregistered)]
    [InlineData(MessagingErrorCode.InvalidArgument)]
    [InlineData(MessagingErrorCode.SenderIdMismatch)]
    public void IsStaleFcmError_PermanentlyInvalidTokens_PrunesSubscription(MessagingErrorCode code) =>
        PushNotificationService.IsStaleFcmError(code).Should().BeTrue();

    [Fact]
    public void CreateFcmMessage_NullUrl_OmitsDestination()
    {
        var message = PushNotificationService.CreateFcmMessage("device-token", "Title", "Body", null);

        message.Token.Should().Be("device-token");
        message.Notification.Title.Should().Be("Title");
        message.Notification.Body.Should().Be("Body");
        message.Data.Should().BeEmpty();
    }

    [Fact]
    public void CreateFcmMessage_RealUrl_PreservesEveryField()
    {
        var message = PushNotificationService.CreateFcmMessage("device-token", "Title", "Body", "/progress");

        message.Token.Should().Be("device-token");
        message.Notification.Title.Should().Be("Title");
        message.Notification.Body.Should().Be("Body");
        message.Data.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>("url", "/progress"));
    }

    [Fact]
    public void CreateWebPushPayload_NullUrl_OmitsDestination()
    {
        using var payload = JsonDocument.Parse(PushNotificationService.CreateWebPushPayload("Title", "Body", null));

        payload.RootElement.EnumerateObject().Should().HaveCount(2);
        payload.RootElement.GetProperty("title").GetString().Should().Be("Title");
        payload.RootElement.GetProperty("body").GetString().Should().Be("Body");
        payload.RootElement.TryGetProperty("url", out _).Should().BeFalse();
    }

    [Fact]
    public void CreateWebPushPayload_RealUrl_PreservesEveryField()
    {
        using var payload = JsonDocument.Parse(PushNotificationService.CreateWebPushPayload("Title", "Body", "/progress"));

        payload.RootElement.EnumerateObject().Should().HaveCount(3);
        payload.RootElement.GetProperty("title").GetString().Should().Be("Title");
        payload.RootElement.GetProperty("body").GetString().Should().Be("Body");
        payload.RootElement.GetProperty("url").GetString().Should().Be("/progress");
    }

    [Fact]
    public void SanitizeForDelivery_StripsControlCharacters()
    {
        var controlChars = new[] { 0, 7, 9, 10, 13, 27, 127, 133 };
        var input = "Hello" + string.Concat(controlChars.Select(c => (char)c)) + "World";

        var result = PushNotificationService.SanitizeForDelivery(input, PushNotificationService.MaxBodyBytes);

        result.Should().Be("HelloWorld");
    }

    [Fact]
    public void SanitizeForDelivery_TruncatesOverlongContentToByteBudget()
    {
        var input = new string('a', 5_000);

        var result = PushNotificationService.SanitizeForDelivery(input, PushNotificationService.MaxTitleBytes);

        result.Length.Should().Be(PushNotificationService.MaxTitleBytes);
        Encoding.UTF8.GetByteCount(result).Should().Be(PushNotificationService.MaxTitleBytes);
    }

    [Fact]
    public void SanitizeForDelivery_NeverSplitsMultiByteRuneAtBoundary()
    {
        var input = string.Concat(Enumerable.Repeat("\U0001F600", 10));

        var result = PushNotificationService.SanitizeForDelivery(input, 10);

        Encoding.UTF8.GetByteCount(result).Should().Be(8);
        result.Should().Be("\U0001F600\U0001F600");
        result.Should().NotContain("�");
    }

    [Fact]
    public void SanitizeForDelivery_PreservesValidContentUnchanged()
    {
        const string input = "Great job! You kept your streak \U0001F525 café";

        var result = PushNotificationService.SanitizeForDelivery(input, PushNotificationService.MaxBodyBytes);

        result.Should().Be(input);
    }

    [Fact]
    public void SanitizeForDelivery_EmptyString_ReturnsEmpty()
    {
        PushNotificationService.SanitizeForDelivery(string.Empty, PushNotificationService.MaxBodyBytes)
            .Should().BeEmpty();
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
        public long? LastRequestContentLength { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (request.Content is not null)
                LastRequestContentLength = request.Content.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult().LongLength;
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
