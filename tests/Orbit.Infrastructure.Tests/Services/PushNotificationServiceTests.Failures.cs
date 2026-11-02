using System.Net;
using System.Text.Json;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class PushNotificationServiceTests
{
    [Theory]
    [InlineData("UNREGISTERED", 1)]
    [InlineData("SENDER_ID_MISMATCH", 1)]
    [InlineData("INVALID_ARGUMENT", 2)]
    [InlineData("QUOTA_EXCEEDED", 2)]
    public async Task Fcm_MixedResponses_PrunesOnlyStaleAndKeepsSuccessfulDelivery(string error, int remaining)
    {
        _dbContext.PushSubscriptions.AddRange(
            PushSubscription.Create(_userId, "live-token", "fcm", "auth").Value,
            PushSubscription.Create(_userId, "failed-token", "fcm", "auth").Value);
        await _dbContext.SaveChangesAsync();
        var handler = new StubHttpMessageHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains("live-token", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"name":"projects/test-project/messages/one"}""") };
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    error = new
                    {
                        status = "INVALID_ARGUMENT",
                        message = "send failed",
                        details = new[] {
                        new Dictionary<string, string> { ["@type"] = "type.googleapis.com/google.firebase.fcm.v1.FcmError", ["errorCode"] = error } }
                    }
                }))
            };
        });
        var app = FirebaseApp.Create(new AppOptions
        {
            Credential = GoogleCredential.FromAccessToken("test-access-token"),
            ProjectId = "test-project",
            HttpClientFactory = new FcmHttpClientFactory(handler)
        });
        try
        {
            var logger = Substitute.For<ILogger<PushNotificationService>>();
            logger.IsEnabled(LogLevel.Warning).Returns(true);
            var service = new PushNotificationService(_dbContext, Options.Create(_vapidSettings), logger,
                new HttpClient(), Substitute.For<IApnsPushSender>(), Substitute.For<IWnsPushSender>());
            (await service.TrySendToUserAsync(_userId, "Title", "Body")).Should().BeTrue();
            (await _dbContext.PushSubscriptions.CountAsync()).Should().Be(remaining);
            if (error == "INVALID_ARGUMENT")
                logger.ReceivedCalls().Should().Contain(c => c.GetMethodInfo().Name == "Log" && (LogLevel)c.GetArguments()[0]! == LogLevel.Warning);
        }
        finally { app.Delete(); }
    }

    [Fact]
    public async Task WebPush_CancelDuringBackoff_Propagates()
    {
        await SeedWebPushSubscription("https://fcm.googleapis.com/sub/backoff");
        using var cts = new CancellationTokenSource();
        var handler = new StubHttpMessageHandler(_ =>
        {
            cts.CancelAfter(30);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        var run = () => CreateService(handler).TrySendToUserAsync(_userId, "Title", "Body", cancellationToken: cts.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
        handler.CallCount.Should().Be(1);
    }
}
