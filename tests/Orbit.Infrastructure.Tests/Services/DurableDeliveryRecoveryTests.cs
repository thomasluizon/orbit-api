using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Orbit.Application.Marketing.Commands;
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
        var client = Substitute.For<IAmazonSimpleEmailServiceV2>();
        client.SendEmailAsync(Arg.Any<SendEmailRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("provider unavailable"));
        var emails = new SesEmailService(client, Options.Create(new SesSettings()),
            Options.Create(new FrontendSettings { BaseUrl = "https://app.useorbit.org" }), NullLogger<SesEmailService>.Instance);
        using (var db = factory.CreateContext())
        {
            var job = new SendMarketingEmailJob(emails, new GenericRepository<MarketingDelivery>(db),
                new UnitOfWork(db, new DatabaseConnectionSettings()), Substitute.For<IMarketingUnsubscribeTokenService>(),
                Options.Create(new MarketingSettings { ApiBaseUrl = "https://api.useorbit.org", SendDelayMilliseconds = 0 }));
            await FluentActions.Invoking(() => job.ExecuteRecipientAsync(delivery.BroadcastId, delivery.RecipientId, delivery.Email, delivery.Language))
                .Should().ThrowAsync<InvalidOperationException>();
        }
        var jobs = Substitute.For<IBackgroundJobClient>();
        jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("101");
        using var provider = Services(factory).AddSingleton(jobs).BuildServiceProvider();
        await new DurableDeliveryRecoveryService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DurableDeliveryRecoveryService>.Instance).RunAsync(default);
        jobs.Received(1).Create(Arg.Is<Job>(job => job.Type == typeof(SendMarketingEmailJob)), Arg.Any<IState>());
        (await factory.Context.MarketingDeliveries.AsNoTracking().SingleAsync()).SentAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData("Enqueued")]
    [InlineData("Processing")]
    [InlineData("Scheduled")]
    public async Task RepeatedRecoveryTicks_QueueAnUnchangedRecipientOnlyOnce(string stateName)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var delivery = MarketingDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), "recipient@example.com", "en",
            "Subject", "Assunto", "<p>Body</p>", "<p>Corpo</p>", DateTime.UtcNow.AddMinutes(-1));
        factory.Context.MarketingDeliveries.Add(delivery);
        await factory.Context.SaveChangesAsync();
        var jobs = Substitute.For<IBackgroundJobClient>();
        jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("101");
        jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("recipient-job");
        var connection = Substitute.For<IStorageConnection>();
        connection.GetStateData("recipient-job").Returns(new StateData { Name = stateName });
        using var provider = Services(factory, connection).AddSingleton(jobs).BuildServiceProvider();
        var recovery = new DurableDeliveryRecoveryService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DurableDeliveryRecoveryService>.Instance);

        for (var tick = 0; tick < 3; tick++)
        {
            using var db = factory.CreateContext();
            await db.MarketingDeliveries.ExecuteUpdateAsync(update => update.SetProperty(row => row.NextRecoveryAtUtc, DateTime.UtcNow.AddMinutes(-1)));
            await recovery.RunAsync(default);
        }

        connection.Received(2).GetStateData("recipient-job");
        jobs.Received(1).Create(Arg.Is<Job>(job => job.Type == typeof(SendMarketingEmailJob)), Arg.Any<IState>());
    }

    [Fact]
    public async Task BroadcastDispatch_PersistsOutstandingJobAndRecoveryDoesNotDuplicateIt()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Recipient", "recipient@example.com").Value;
        user.SetMarketingConsent(true);
        factory.Context.Users.Add(user);
        await factory.Context.SaveChangesAsync();
        var jobs = Substitute.For<IBackgroundJobClient>();
        jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("101");
        using (var db = factory.CreateContext())
        {
            var handler = new SendMarketingBroadcastCommandHandler(new GenericRepository<User>(db),
                new GenericRepository<MarketingContact>(db), jobs, Substitute.For<IMarketingUnsubscribeTokenService>(),
                new GenericRepository<MarketingDelivery>(db), new UnitOfWork(db, new DatabaseConnectionSettings()),
                Options.Create(new MarketingSettings { SendDelayMilliseconds = 0 }),
                NullLogger<SendMarketingBroadcastCommandHandler>.Instance);
            (await handler.Handle(new("Subject", "Assunto", "<p>Body</p>", "<p>Corpo</p>", null), default)).IsSuccess.Should().BeTrue();
        }
        using (var db = factory.CreateContext())
        {
            (await db.MarketingDeliveries.SingleAsync()).BackgroundJobId.Should().Be("101");
            await db.MarketingDeliveries.ExecuteUpdateAsync(update => update.SetProperty(row => row.NextRecoveryAtUtc, DateTime.UtcNow.AddMinutes(-1)));
        }
        var connection = Substitute.For<IStorageConnection>();
        connection.GetStateData("101").Returns(new StateData { Name = new EnqueuedState().Name });
        using var provider = Services(factory, connection).AddSingleton(jobs).BuildServiceProvider();
        var recovery = new DurableDeliveryRecoveryService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DurableDeliveryRecoveryService>.Instance);
        await recovery.RunAsync(default);
        await recovery.RunAsync(default);

        jobs.Received(1).Create(Arg.Any<Job>(), Arg.Any<IState>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Failed")]
    [InlineData("Deleted")]
    [InlineData("Succeeded")]
    public async Task Recovery_ReplacesAbandonedDispatch(string? stateName)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var delivery = MarketingDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), "recipient@example.com", "en",
            "Subject", "Assunto", "<p>Body</p>", "<p>Corpo</p>", DateTime.UtcNow.AddMinutes(-10));
        delivery.RecordDispatch("101", DateTime.UtcNow.AddMinutes(-10));
        factory.Context.MarketingDeliveries.Add(delivery);
        await factory.Context.SaveChangesAsync();
        var connection = Substitute.For<IStorageConnection>();
        connection.GetStateData("101").Returns(stateName is null ? null : new StateData { Name = stateName });
        var jobs = Substitute.For<IBackgroundJobClient>();
        jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("102");
        using var provider = Services(factory, connection).AddSingleton(jobs).BuildServiceProvider();
        var recovery = new DurableDeliveryRecoveryService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DurableDeliveryRecoveryService>.Instance);
        await recovery.RunAsync(default);
        await recovery.RunAsync(default);

        jobs.Received(1).Create(Arg.Any<Job>(), Arg.Any<IState>());
        using var reader = factory.CreateContext();
        (await reader.MarketingDeliveries.SingleAsync()).BackgroundJobId.Should().Be("102");
    }

    [Fact]
    public async Task Recovery_BoundsEachTickAndRecoversTheRestOnTheNextTick()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        for (var i = 0; i < 105; i++)
            factory.Context.MarketingDeliveries.Add(MarketingDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), $"recipient{i}@example.com", "en",
                "Subject", "Assunto", "<p>Body</p>", "<p>Corpo</p>", DateTime.UtcNow.AddMinutes(-1)));
        await factory.Context.SaveChangesAsync();
        var jobs = Substitute.For<IBackgroundJobClient>();
        jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("101");
        using var provider = Services(factory).AddSingleton(jobs).BuildServiceProvider();
        var recovery = new DurableDeliveryRecoveryService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DurableDeliveryRecoveryService>.Instance);
        await recovery.RunAsync(default);
        jobs.Received(100).Create(Arg.Any<Job>(), Arg.Any<IState>());
        await recovery.RunAsync(default);
        await recovery.RunAsync(default);
        jobs.Received(105).Create(Arg.Any<Job>(), Arg.Any<IState>());
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

    private static ServiceCollection Services(SqliteOrbitDbContextFactory factory, IStorageConnection? connection = null)
    {
        var services = new ServiceCollection();
        var storage = Substitute.For<JobStorage>();
        storage.GetConnection().Returns(connection ?? Substitute.For<IStorageConnection>());
        services.AddSingleton(storage);
        services.AddScoped(_ => factory.CreateContext());
        services.AddScoped<IUnitOfWork>(sp => new UnitOfWork(sp.GetRequiredService<OrbitDbContext>(), new DatabaseConnectionSettings()));
        return services;
    }
}
