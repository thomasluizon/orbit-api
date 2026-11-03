using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class PushNotificationServiceTests
{
    [Theory]
    [InlineData("apns")]
    [InlineData("wns")]
    [InlineData("web")]
    [InlineData("fcm")]
    public async Task DeliveryProgress_MultipleDevices_CheckpointsEachDeliveryOnce(string transport)
    {
        for (var i = 0; i < 2; i++)
        {
            var endpoint = transport switch
            {
                "web" => $"https://fcm.googleapis.com/sub/progress-{i}",
                "wns" => $"https://notify.windows.com/progress-{i}",
                _ => $"device-{i}"
            };
            _dbContext.PushSubscriptions.Add(PushSubscription.Create(_userId, endpoint,
                transport == "web" ? _receiverP256dh : transport, _receiverAuth,
                deviceId: transport == "wns" ? Guid.NewGuid() : null).Value);
        }
        await _dbContext.SaveChangesAsync();
        var apns = Substitute.For<IApnsPushSender>();
        apns.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>())
            .Returns(ApnsDeliveryOutcome.Delivered);
        var wns = Substitute.For<IWnsPushSender>();
        wns.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(WnsDeliveryOutcome.Delivered);
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var fcmHandler = new StubHttpMessageHandler(_ => FcmSuccess());
        var app = transport == "fcm" ? CreateFirebase(fcmHandler) : null;
        try
        {
            using var service = new PushNotificationService(_dbContext, Options.Create(_vapidSettings),
                NullLogger<PushNotificationService>.Instance, new HttpClient(handler), apns, wns);
            var progress = new ConcurrentBag<int>();
            var delivered = await service.TrySendManyAsync([
                new PushDelivery(_userId, "First", "Body"), new PushDelivery(_userId, "Second", "Body")], index =>
            {
                progress.Add(index);
                return Task.CompletedTask;
            });

            delivered.Should().BeEquivalentTo([0, 1]);
            progress.Should().BeEquivalentTo([0, 1]);
            if (transport == "fcm") fcmHandler.CallCount.Should().Be(4);
        }
        finally { app?.Delete(); }
    }

    [Fact]
    public async Task DeliveryProgress_Fcm_CheckpointsBeforeAnotherDeviceIsCancelled()
    {
        _dbContext.PushSubscriptions.AddRange(
            PushSubscription.Create(_userId, "hang", "fcm", "auth").Value,
            PushSubscription.Create(_userId, "live", "fcm", "auth").Value);
        await _dbContext.SaveChangesAsync();
        var handler = new BlockingFcmHandler();
        var app = CreateFirebase(handler);
        try
        {
            using var cancellation = new CancellationTokenSource();
            using var service = new PushNotificationService(_dbContext, Options.Create(_vapidSettings),
                NullLogger<PushNotificationService>.Instance, new HttpClient(), Substitute.For<IApnsPushSender>(), Substitute.For<IWnsPushSender>());
            var checkpoint = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var progress = new ConcurrentBag<int>();
            var send = service.TrySendManyAsync([new PushDelivery(_userId, "Title", "Body")], index =>
            {
                progress.Add(index);
                checkpoint.TrySetResult();
                return Task.CompletedTask;
            }, cancellation.Token);
            try
            {
                await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await checkpoint.Task.WaitAsync(TimeSpan.FromSeconds(5));
                handler.LiveDeliveries.Should().Be(1);
                send.IsCompleted.Should().BeFalse();
            }
            finally { cancellation.Cancel(); }

            var observe = async () => await send;
            await observe.Should().ThrowAsync<OperationCanceledException>();
            progress.Should().Equal(0);
        }
        finally { app.Delete(); }
    }

    [Fact]
    public async Task DeliveryProgress_AcceptedAsCallerCancels_StillCheckpointsAndAwaitsCompletion()
    {
        _dbContext.PushSubscriptions.Add(PushSubscription.Create(_userId, "apns-token", "apns", "auth").Value);
        await _dbContext.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        var sender = Substitute.For<IApnsPushSender>();
        sender.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return ApnsDeliveryOutcome.Delivered;
            });
        using var service = new PushNotificationService(_dbContext, Options.Create(_vapidSettings),
            NullLogger<PushNotificationService>.Instance, new HttpClient(), sender, Substitute.For<IWnsPushSender>());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var send = service.TrySendManyAsync([new PushDelivery(_userId, "Title", "Body")], async index =>
        {
            index.Should().Be(0);
            cancellation.IsCancellationRequested.Should().BeTrue();
            entered.TrySetResult();
            await release.Task;
        }, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            send.IsCompleted.Should().BeFalse();
        }
        finally { release.TrySetResult(); }

        var observe = async () => await send;
        await observe.Should().ThrowAsync<OperationCanceledException>();
    }
}
