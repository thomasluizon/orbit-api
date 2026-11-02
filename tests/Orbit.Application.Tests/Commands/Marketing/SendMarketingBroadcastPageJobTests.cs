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
    public async Task Execute_BlockedProvider_BoundsParallelismAndRetriesOnlyItsPage()
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
        var job = new SendMarketingBroadcastPageJob(email, tokens, Options.Create(new MarketingSettings { BroadcastParallelism = 3 }), TimeProvider.System);
        MarketingRecipient[] page = [new(Guid.NewGuid(), "fail@example.com", "en"), new(Guid.NewGuid(), "second@example.com", "en"), new(Guid.NewGuid(), "third@example.com", "pt-BR")];
        var run = job.ExecuteAsync(Guid.NewGuid(), 1, Content, page, default);
        try { await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { release.SetResult(); }
        var finish = () => run;
        await finish.Should().ThrowAsync<AggregateException>();
        maxActive.Should().Be(3);
        await email.Received(3).SendMarketingEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        email.ClearReceivedCalls();
        email.SendMarketingEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await job.ExecuteAsync(Guid.NewGuid(), 1, Content, page, default);
        await email.Received(3).SendMarketingEmailAsync(Arg.Is<string>(address => page.Any(recipient => recipient.Email == address)), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_InvalidPageOrCancellation_DoesNotDeliver()
    {
        var email = Substitute.For<IEmailService>();
        var job = new SendMarketingBroadcastPageJob(email, Substitute.For<IMarketingUnsubscribeTokenService>(), Options.Create(new MarketingSettings()), TimeProvider.System);
        var invalid = () => job.ExecuteAsync(Guid.Empty, 0, Content, [], default);
        await invalid.Should().ThrowAsync<ArgumentException>();
        var canceled = () => job.ExecuteAsync(Guid.NewGuid(), 0, Content, [new(Guid.NewGuid(), "a@example.com", "en")], new CancellationToken(true));
        await canceled.Should().ThrowAsync<OperationCanceledException>();
        await email.DidNotReceiveWithAnyArgs().SendMarketingEmailAsync(default!, default!, default!, default!, default!, default);
    }
}
