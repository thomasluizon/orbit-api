using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Marketing.Commands;
using Orbit.Application.Marketing.Jobs;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Marketing;

public sealed class SendMarketingBroadcastPageJobTests
{
    private static SendMarketingBroadcastCommand Content => new("Subject", "Assunto", "<p>Body</p>", "<p>Corpo</p>", null);

    [Fact]
    public async Task Execute_BlockedProvider_BoundsParallelismAndRetriesOnlyFailedRecipients()
    {
        var email = Substitute.For<IEmailService>();
        var tokens = Substitute.For<IMarketingUnsubscribeTokenService>();
        tokens.CreateToken(Arg.Any<Guid>()).Returns("signed");
        var active = 0;
        var maxActive = 0;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        email.SendMarketingEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var count = Interlocked.Increment(ref active);
                lock (release) maxActive = Math.Max(maxActive, count);
                if (count == 3) reached.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(call.Arg<CancellationToken>());
                    if (call.ArgAt<string>(0) == "fail@example.com") throw new InvalidOperationException("provider failed");
                }
                finally { Interlocked.Decrement(ref active); }
            });
        var deliveries = CreateStore();
        var job = new SendMarketingBroadcastPageJob(email, tokens, Options.Create(new MarketingSettings { BroadcastParallelism = 3 }), TimeProvider.System, deliveries);
        MarketingRecipient[] page = [new(Guid.NewGuid(), "fail@example.com", "en"), new(Guid.NewGuid(), "second@example.com", "en"), new(Guid.NewGuid(), "third@example.com", "pt-BR")];
        var broadcastId = Guid.NewGuid();
        var run = job.ExecuteAsync(broadcastId, 1, Content, page, default);
        try { await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { release.SetResult(); }
        var finish = () => run;
        await finish.Should().ThrowAsync<AggregateException>();
        maxActive.Should().Be(3);
        await email.Received(3).SendMarketingEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        email.ClearReceivedCalls();
        email.SendMarketingEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        job = new SendMarketingBroadcastPageJob(email, tokens, Options.Create(new MarketingSettings { BroadcastParallelism = 3 }), TimeProvider.System, deliveries);
        await job.ExecuteAsync(broadcastId, 1, Content, page, default);
        await email.Received(1).SendMarketingEmailAsync("fail@example.com", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await email.Received(1).SendMarketingEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_CancelledPage_ResumesWithoutResendingSuccessfulRecipients()
    {
        var email = Substitute.For<IEmailService>();
        using var cancellation = new CancellationTokenSource();
        email.SendMarketingEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (call.ArgAt<string>(0) == "second@example.com")
                {
                    cancellation.Cancel();
                    return Task.FromCanceled(call.Arg<CancellationToken>());
                }
                return Task.CompletedTask;
            });
        var deliveries = CreateStore();
        var job = new SendMarketingBroadcastPageJob(email, Substitute.For<IMarketingUnsubscribeTokenService>(),
            Options.Create(new MarketingSettings { BroadcastParallelism = 1 }), TimeProvider.System, deliveries);
        var broadcastId = Guid.NewGuid();
        MarketingRecipient[] page = [new(Guid.NewGuid(), "first@example.com", "en"), new(Guid.NewGuid(), "second@example.com", "en"), new(Guid.NewGuid(), "third@example.com", "en")];

        var interrupted = () => job.ExecuteAsync(broadcastId, 0, Content, page, cancellation.Token);
        await interrupted.Should().ThrowAsync<OperationCanceledException>();
        email.ClearReceivedCalls();
        email.SendMarketingEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        job = new SendMarketingBroadcastPageJob(email, Substitute.For<IMarketingUnsubscribeTokenService>(),
            Options.Create(new MarketingSettings { BroadcastParallelism = 1 }), TimeProvider.System, deliveries);
        await job.ExecuteAsync(broadcastId, 0, Content, page, default);

        await email.DidNotReceive().SendMarketingEmailAsync("first@example.com", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await email.Received(2).SendMarketingEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_InvalidPageOrCancellation_DoesNotDeliver()
    {
        var email = Substitute.For<IEmailService>();
        var job = new SendMarketingBroadcastPageJob(email, Substitute.For<IMarketingUnsubscribeTokenService>(), Options.Create(new MarketingSettings()), TimeProvider.System, CreateStore());
        var invalid = () => job.ExecuteAsync(Guid.Empty, 0, Content, [], default);
        await invalid.Should().ThrowAsync<ArgumentException>();
        var canceled = () => job.ExecuteAsync(Guid.NewGuid(), 0, Content, [new(Guid.NewGuid(), "a@example.com", "en")], new CancellationToken(true));
        await canceled.Should().ThrowAsync<OperationCanceledException>();
        await email.DidNotReceiveWithAnyArgs().SendMarketingEmailAsync(default!, default!, default!, default!, default!, default);
    }

    private static IMarketingBroadcastDeliveryStore CreateStore()
    {
        var delivered = new ConcurrentDictionary<(Guid Broadcast, Guid Recipient), DateTime>();
        var store = Substitute.For<IMarketingBroadcastDeliveryStore>();
        store.GetDeliveredRecipientIdsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlySet<Guid>)delivered.Keys.Where(key => key.Broadcast == call.Arg<Guid>())
                .Select(key => key.Recipient).ToHashSet());
        store.RecordDeliveredAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                delivered.TryAdd((call.ArgAt<Guid>(0), call.ArgAt<Guid>(1)), call.Arg<DateTime>());
                return Task.CompletedTask;
            });
        return store;
    }
}
