using NSubstitute;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Services;

internal static class PushBatchTestAdapter
{
    internal static void ForwardToSingleSends(IPushNotificationService push)
    {
        push.TrySendManyAsync(Arg.Any<IReadOnlyList<PushDelivery>>(), Arg.Any<Func<int, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var delivered = await push.TrySendManyAsync(call.Arg<IReadOnlyList<PushDelivery>>(), call.Arg<CancellationToken>());
                foreach (var index in delivered) await call.Arg<Func<int, Task>>()(index);
                return delivered;
            });
        push.SendManyAsync(Arg.Any<IReadOnlyList<PushDelivery>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                foreach (var delivery in call.Arg<IReadOnlyList<PushDelivery>>())
                {
                    try
                    {
                        if (delivery.Data is { } data)
                            await push.TrySendReminderToUserAsync(delivery.UserId, delivery.Title, delivery.Body, data, delivery.Url, call.Arg<CancellationToken>());
                        else
                            await push.SendToUserAsync(delivery.UserId, delivery.Title, delivery.Body, delivery.Url, call.Arg<CancellationToken>());
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        continue;
                    }
                }
            });
        push.TrySendManyAsync(Arg.Any<IReadOnlyList<PushDelivery>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var delivered = new HashSet<int>();
                var deliveries = call.Arg<IReadOnlyList<PushDelivery>>();
                for (var i = 0; i < deliveries.Count; i++)
                {
                    var delivery = deliveries[i];
                    try
                    {
                        var sent = delivery.Data is { } data
                            ? await push.TrySendReminderToUserAsync(delivery.UserId, delivery.Title, delivery.Body, data, delivery.Url, call.Arg<CancellationToken>())
                            : await push.TrySendToUserAsync(delivery.UserId, delivery.Title, delivery.Body, delivery.Url, call.Arg<CancellationToken>());
                        if (sent) delivered.Add(i);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { continue; }
                }
                return (IReadOnlySet<int>)delivered;
            });
    }
}
