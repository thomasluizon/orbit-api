using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Marketing.Commands;
using Orbit.Application.Marketing.Jobs;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public sealed class MarketingBroadcastDeliveryStoreTests
{
    [Fact]
    public async Task RetryInNewScope_OnlyUndeliveredRecipientIsSent_AndBroadcastsAreIndependent()
    {
        using var factory = new SqliteOrbitDbContextFactory(Path.Combine(Path.GetTempPath(), $"orbit-marketing-{Guid.NewGuid():N}.db"));
        using var services = new ServiceCollection().AddScoped(_ => factory.CreateContext())
            .AddScoped<IMarketingBroadcastDeliveryStore, MarketingBroadcastDeliveryStore>().BuildServiceProvider();
        var email = Substitute.For<IEmailService>();
        email.SendMarketingEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(0) == "failed@example.com" ? Task.FromException(new InvalidOperationException("SES unavailable")) : Task.CompletedTask);
        var broadcastId = Guid.NewGuid();
        MarketingRecipient[] recipients = [new(Guid.NewGuid(), "delivered@example.com", "en"), new(Guid.NewGuid(), "failed@example.com", "en")];
        var content = new SendMarketingBroadcastCommand("Subject", "Assunto", "Body", "Corpo", null);
        SendMarketingBroadcastPageJob Job(IServiceProvider scope) => new(email, Substitute.For<IMarketingUnsubscribeTokenService>(),
            Options.Create(new MarketingSettings { BroadcastParallelism = 2 }), TimeProvider.System,
            scope.GetRequiredService<IMarketingBroadcastDeliveryStore>());
        using (var first = services.CreateScope())
        {
            var run = () => Job(first.ServiceProvider).ExecuteAsync(broadcastId, 0, content, recipients, default);
            await run.Should().ThrowAsync<AggregateException>();
        }
        (await factory.Context.MarketingBroadcastDeliveries.CountAsync()).Should().Be(1);
        email.ClearReceivedCalls();
        email.SendMarketingEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        using (var retry = services.CreateScope())
            await Job(retry.ServiceProvider).ExecuteAsync(broadcastId, 0, content, recipients, default);

        await email.Received(1).SendMarketingEmailAsync("failed@example.com", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await email.DidNotReceive().SendMarketingEmailAsync("delivered@example.com", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        (await factory.Context.MarketingBroadcastDeliveries.CountAsync()).Should().Be(2);
        using (var next = services.CreateScope())
            await Job(next.ServiceProvider).ExecuteAsync(Guid.NewGuid(), 0, content, recipients, default);
        (await factory.Context.MarketingBroadcastDeliveries.CountAsync()).Should().Be(4);
    }

    [Fact]
    public async Task RepeatedCheckpoint_KeepsFirstSuccessfulDeliveryInstant()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        using var services = new ServiceCollection().AddScoped(_ => factory.CreateContext()).BuildServiceProvider();
        var store = new MarketingBroadcastDeliveryStore(services.GetRequiredService<IServiceScopeFactory>());
        var broadcastId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        var deliveredAtUtc = new DateTime(2030, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        await store.RecordDeliveredAsync(broadcastId, recipientId, deliveredAtUtc, default);
        await store.RecordDeliveredAsync(broadcastId, recipientId, deliveredAtUtc.AddMinutes(1), default);

        (await factory.Context.MarketingBroadcastDeliveries.SingleAsync()).DeliveredAtUtc.Should().Be(deliveredAtUtc);
        (await store.GetDeliveredRecipientIdsAsync(broadcastId, [recipientId, Guid.NewGuid()], default)).Should().Equal(recipientId);
        (await store.GetDeliveredRecipientIdsAsync(Guid.NewGuid(), [recipientId], default)).Should().BeEmpty();
    }
}
