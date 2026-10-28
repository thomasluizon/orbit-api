using System.Text.Json;
using FirebaseAdmin.Messaging;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class PushNotificationServiceTests
{
    [Fact]
    public async Task RepeatMetadata_ReachesDesktopTransportsAndTheFcmAndWebPayloads()
    {
        _dbContext.PushSubscriptions.AddRange(
            PushSubscription.Create(_userId, "ios-token", "apns", "apns").Value,
            PushSubscription.Create(_userId, "https://dm3p.notify.windows.com/?token=channel", "wns", "wns", deviceId: Guid.NewGuid()).Value);
        await _dbContext.SaveChangesAsync();
        var apns = Substitute.For<IApnsPushSender>();
        var wns = Substitute.For<IWnsPushSender>();
        var data = new Dictionary<string, string>
        {
            ["habitId"] = Guid.NewGuid().ToString(),
            ["reminderAtUtc"] = "2027-09-26T12:00:00.0000000Z",
            ["tag"] = "orbit-repeat-habit",
            ["doneToken"] = "signed-action",
            ["doneExpiresAtUtc"] = "2027-09-26T20:00:00.0000000Z",
            ["doneLabel"] = "Concluir"
        };
        var service = new PushNotificationService(_dbContext, Options.Create(_vapidSettings),
            NullLogger<PushNotificationService>.Instance, new HttpClient(), apns, wns);
        await service.SendReminderToUserAsync(_userId, "Água", "Próximo às 20:00", data, "/");
        await apns.Received(1).SendAsync("ios-token", "Água", "Próximo às 20:00", "/", Arg.Any<CancellationToken>(), null, data);
        var payload = PushNotificationService.CreateWebPushPayload("Água", "Próximo às 20:00", "/", data);
        await wns.Received(1).SendAsync(Arg.Any<string>(), payload, Arg.Any<CancellationToken>());
        var fcm = PushNotificationService.CreateFcmMessage("token", "Água", "Próximo às 20:00", "/", data);
        using (new AssertionScope())
        {
            fcm.Notification.Should().BeNull();
            fcm.Android.Priority.Should().Be(Priority.High);
            fcm.Android.Notification.Should().BeNull();
            fcm.Data["tag"].Should().Be(data["tag"]);
            fcm.Data.Should().Contain("title", "Água");
            fcm.Data.Should().Contain("message", "Próximo às 20:00");
            fcm.Data.Should().Contain("categoryId", "orbit-repeat-reminder");
            fcm.Data.Should().Contain("channelId", "default");
            fcm.Data.Should().Contain("url", "/");
            fcm.Data.Should().HaveCount(data.Count + 5);
            data.Should().HaveCount(6);
        }
        using var web = JsonDocument.Parse(payload);
        using var apple = JsonDocument.Parse(JsonSerializer.Serialize(ApnsPushSender.CreatePayload("Água", "Próximo às 20:00", "/", data)));
        foreach (var (key, value) in data)
        {
            fcm.Data[key].Should().Be(value);
            web.RootElement.GetProperty(key).GetString().Should().Be(value);
            apple.RootElement.GetProperty("body").GetProperty(key).GetString().Should().Be(value);
        }
    }

    [Fact]
    public void CreateFcmMessage_OrdinaryReminder_KeepsOsRenderedNotification()
    {
        var message = PushNotificationService.CreateFcmMessage("token", "Água", "Próximo às 20:00", "/");

        message.Notification.Title.Should().Be("Água");
        message.Notification.Body.Should().Be("Próximo às 20:00");
        message.Android.Should().BeNull();
        message.Data.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>("url", "/"));
    }
}
