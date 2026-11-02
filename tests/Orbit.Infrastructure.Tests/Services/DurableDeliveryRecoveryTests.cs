using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Common;
using Orbit.Application.Marketing.Jobs;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class DurableDeliveryRecoveryTests
{
    [Fact]
    public async Task RecipientJob_RendersInTheJobAndDoesNotRepeatARecordedSuccess()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var delivery = MarketingDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), "recipient@example.com", "pt-BR",
            "Subject", "Assunto", "<p>Body</p>", "<p>Corpo</p>", DateTime.UtcNow.AddMinutes(-1));
        factory.Context.MarketingDeliveries.Add(delivery);
        await factory.Context.SaveChangesAsync();
        var emails = Substitute.For<IEmailService>();
        var tokens = Substitute.For<IMarketingUnsubscribeTokenService>();
        tokens.CreateToken(delivery.RecipientId).Returns("unsubscribe-token");
        for (var run = 0; run < 2; run++)
        {
            using var db = factory.CreateContext();
            var job = new SendMarketingEmailJob(emails, new GenericRepository<MarketingDelivery>(db),
                new UnitOfWork(db, new DatabaseConnectionSettings()), tokens,
                Options.Create(new MarketingSettings { ApiBaseUrl = "https://api.useorbit.org", SendDelayMilliseconds = 0 }));
            await job.ExecuteRecipientAsync(delivery.BroadcastId, delivery.RecipientId, delivery.Email, delivery.Language);
        }
        await emails.Received(1).SendMarketingEmailAsync(delivery.Email, "Assunto", "<p>Corpo</p>", "pt-BR",
            "https://api.useorbit.org/api/marketing/unsubscribe?token=unsubscribe-token&lang=pt", Arg.Any<CancellationToken>());
        (await factory.Context.MarketingDeliveries.AsNoTracking().SingleAsync()).SentAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task SendFailure_LeavesTheRecipientPendingAndARecoveryTickQueuesIt()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var delivery = MarketingDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), "recipient@example.com", "en",
            "Subject", "Assunto", "<p>Body</p>", "<p>Corpo</p>", DateTime.UtcNow.AddMinutes(-1));
        factory.Context.MarketingDeliveries.Add(delivery);
        await factory.Context.SaveChangesAsync();
        var emails = Substitute.For<IEmailService>();
        emails.SendMarketingEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("provider unavailable"));
        using (var db = factory.CreateContext())
        {
            var job = new SendMarketingEmailJob(emails, new GenericRepository<MarketingDelivery>(db),
                new UnitOfWork(db, new DatabaseConnectionSettings()), Substitute.For<IMarketingUnsubscribeTokenService>(),
                Options.Create(new MarketingSettings { ApiBaseUrl = "https://api.useorbit.org", SendDelayMilliseconds = 0 }));
            await FluentActions.Invoking(() => job.ExecuteRecipientAsync(delivery.BroadcastId, delivery.RecipientId, delivery.Email, delivery.Language))
                .Should().ThrowAsync<InvalidOperationException>();
        }
        var jobs = Substitute.For<IBackgroundJobClient>();
        using var provider = Services(factory).AddSingleton(jobs).BuildServiceProvider();
        await new DurableDeliveryRecoveryService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DurableDeliveryRecoveryService>.Instance).RunAsync(default);
        jobs.Received(1).Create(Arg.Is<Job>(job => job.Type == typeof(SendMarketingEmailJob)), Arg.Any<IState>());
        (await factory.Context.MarketingDeliveries.AsNoTracking().SingleAsync()).SentAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task FailedCouponCancel_RemainsDurableUntilTheNextSuccessfulAttempt()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var pending = PendingCouponCancellation.Create(Guid.NewGuid(), "coupon-consumed-by-play");
        factory.Context.PendingCouponCancellations.Add(pending);
        await factory.Context.SaveChangesAsync();
        var rewards = Substitute.For<IReferralRewardService>();
        rewards.CancelCouponAsync(pending.CouponId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("billing unavailable"));
        using var provider = Services(factory).AddSingleton(Substitute.For<IBackgroundJobClient>())
            .AddSingleton(rewards).BuildServiceProvider();
        var recovery = new DurableDeliveryRecoveryService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DurableDeliveryRecoveryService>.Instance);
        await recovery.RunAsync(default);
        using (var reader = factory.CreateContext())
        {
            var row = await reader.PendingCouponCancellations.SingleAsync();
            row.Attempts.Should().Be(1);
            row.NextAttemptAtUtc.Should().BeAfter(DateTime.UtcNow);
            await reader.PendingCouponCancellations.ExecuteUpdateAsync(update => update.SetProperty(row => row.NextAttemptAtUtc, DateTime.UtcNow.AddSeconds(-1)));
        }
        rewards.CancelCouponAsync(pending.CouponId, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await recovery.RunAsync(default);
        using var final = factory.CreateContext();
        (await final.PendingCouponCancellations.AnyAsync()).Should().BeFalse();
        await rewards.Received(2).CancelCouponAsync(pending.CouponId, Arg.Any<CancellationToken>());
    }

    private static ServiceCollection Services(SqliteOrbitDbContextFactory factory)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => factory.CreateContext());
        services.AddScoped<IUnitOfWork>(sp => new UnitOfWork(sp.GetRequiredService<OrbitDbContext>(), new DatabaseConnectionSettings()));
        return services;
    }
}
