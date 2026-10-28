using System.Text.Json;
using FluentAssertions;
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
        fcm.Android.Notification.Tag.Should().Be(data["tag"]);
        using var web = JsonDocument.Parse(payload);
        using var apple = JsonDocument.Parse(JsonSerializer.Serialize(ApnsPushSender.CreatePayload("Água", "Próximo às 20:00", "/", data)));
        foreach (var (key, value) in data)
        {
            fcm.Data[key].Should().Be(value);
            web.RootElement.GetProperty(key).GetString().Should().Be(value);
            apple.RootElement.GetProperty("body").GetProperty(key).GetString().Should().Be(value);
        }
    }
}
