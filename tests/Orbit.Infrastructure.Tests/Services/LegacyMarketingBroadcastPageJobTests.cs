using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Marketing.Jobs;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class LegacyMarketingBroadcastPageJobTests
{
    [Fact]
    public async Task SerializedLegacyPage_ResumesOnlyUnsentRecipients_AndRetriesAcrossScopesAddNothing()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var invocation = LoadInvocation();
        var broadcastId = (Guid)invocation.Args[0];
        var recipients = (MarketingRecipient[])invocation.Args[3];
        await CreateLegacyTableAsync(factory.Context);
        await factory.Context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MarketingBroadcastDeliveries" ("Id", "BroadcastId", "RecipientId", "DeliveredAtUtc")
            VALUES ({Guid.NewGuid()}, {broadcastId}, {recipients[0].UserId}, {DateTime.UnixEpoch})
            """);
        var queued = new List<Job>();
        var jobs = Substitute.For<IBackgroundJobClient>();
        jobs.Create(Arg.Do<Job>(job => queued.Add(job)), Arg.Any<IState>()).Returns("101");
        var storage = Substitute.For<JobStorage>();
        storage.GetConnection().Returns(Substitute.For<IStorageConnection>());
        var email = Substitute.For<IEmailService>();
        using var services = Services(factory, jobs, storage, email);

        await RunLegacyInvocationAsync(services);
        var imported = await factory.Context.MarketingDeliveries.AsNoTracking().ToListAsync();
        imported.Select(row => row.RecipientId).Should().BeEquivalentTo(recipients.Skip(1).Select(recipient => recipient.UserId));
        imported.Should().OnlyContain(row => row.BroadcastId == broadcastId && row.SentAtUtc == null && row.BackgroundJobId == null);
        imported.Single(row => row.Language == "pt-BR").SubjectPt.Should().Be("Assunto");

        await RunLegacyInvocationAsync(services);
        (await factory.Context.MarketingDeliveries.Select(row => row.Id).ToListAsync())
            .Should().BeEquivalentTo(imported.Select(row => row.Id));
        await email.DidNotReceiveWithAnyArgs().SendMarketingEmailAsync(default!, default!, default!, default!, default!, default);

        var recovery = new DurableDeliveryRecoveryService(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DurableDeliveryRecoveryService>.Instance);
        await recovery.RunAsync(default);
        queued.Should().HaveCount(2).And.OnlyContain(job => job.Type == typeof(SendMarketingEmailJob)
            && job.Method.Name == nameof(SendMarketingEmailJob.ExecuteRecipientAsync));
        foreach (var job in queued)
        {
            using var scope = services.CreateScope();
            await InvokeAsync(scope.ServiceProvider, job);
        }

        await RunLegacyInvocationAsync(services);
        (await factory.Context.MarketingDeliveries.AsNoTracking().ToListAsync()).Should().HaveCount(2)
            .And.OnlyContain(row => row.SentAtUtc != null);
        await email.Received(1).SendMarketingEmailAsync(recipients[1].Email, "Assunto", "<p>Corpo</p>", "pt-BR",
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await email.Received(1).SendMarketingEmailAsync(recipients[2].Email, "Subject", "<p>Body</p>", "en",
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await email.DidNotReceive().SendMarketingEmailAsync(recipients[0].Email, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SerializedLegacyPage_PreservesAnExistingRecipientDelivery()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var invocation = LoadInvocation();
        var recipient = ((MarketingRecipient[])invocation.Args[3])[1];
        var existing = MarketingDelivery.Create((Guid)invocation.Args[0], recipient.UserId, recipient.Email, recipient.Language,
            "Existing subject", "Assunto", "<p>Body</p>", "<p>Corpo</p>", DateTime.UnixEpoch);
        factory.Context.MarketingDeliveries.Add(existing);
        await factory.Context.SaveChangesAsync();
        await CreateLegacyTableAsync(factory.Context);
        using var services = Services(factory, Substitute.For<IBackgroundJobClient>(), Substitute.For<JobStorage>(), Substitute.For<IEmailService>());

        await RunLegacyInvocationAsync(services);
        await RunLegacyInvocationAsync(services);

        var rows = await factory.Context.MarketingDeliveries.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(3);
        rows.Single(row => row.RecipientId == recipient.UserId).Id.Should().Be(existing.Id);
        rows.Single(row => row.RecipientId == recipient.UserId).SubjectEn.Should().Be("Existing subject");
    }

    [Fact]
    public async Task SerializedLegacyPage_AfterExpiry_DoesNotImportRecipients()
    {
        var store = Substitute.For<ILegacyMarketingBroadcastPageStore>();
        var invocation = LoadInvocation();
        var job = new SendMarketingBroadcastPageJob(store, new FixedClock((DateTime)invocation.Args[4]));

        await (Task)invocation.Method.Invoke(job, invocation.Args.ToArray())!;

        await store.DidNotReceiveWithAnyArgs().ImportAsync(default, default!, default!, default, default);
    }

    private static ServiceProvider Services(SqliteOrbitDbContextFactory factory, IBackgroundJobClient jobs, JobStorage storage, IEmailService email)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => factory.CreateContext());
        services.AddScoped<IUnitOfWork>(provider => new UnitOfWork(provider.GetRequiredService<OrbitDbContext>(), new DatabaseConnectionSettings()));
        services.AddScoped<ILegacyMarketingBroadcastPageStore, LegacyMarketingBroadcastPageStore>();
        services.AddScoped<SendMarketingBroadcastPageJob>();
        services.AddScoped<SendMarketingEmailJob>();
        services.AddScoped<IGenericRepository<MarketingDelivery>, GenericRepository<MarketingDelivery>>();
        services.AddSingleton<TimeProvider>(new FixedClock(DateTime.UnixEpoch));
        services.AddSingleton(jobs);
        services.AddSingleton(storage);
        services.AddSingleton(email);
        services.AddSingleton(Substitute.For<IMarketingUnsubscribeTokenService>());
        services.AddSingleton(Options.Create(new MarketingSettings()));
        return services.BuildServiceProvider();
    }

    private static async Task CreateLegacyTableAsync(OrbitDbContext db)
    {
        var migration = new PersistMarketingBroadcastDeliveries();
        foreach (var command in db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, db.Model))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
    }

    private static Job LoadInvocation() => InvocationData.DeserializePayload(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Marketing", "LegacyBroadcastPage.json"))).DeserializeJob();

    private static async Task RunLegacyInvocationAsync(ServiceProvider services)
    {
        using var scope = services.CreateScope();
        await InvokeAsync(scope.ServiceProvider, LoadInvocation());
    }

    private static Task InvokeAsync(IServiceProvider services, Job job) =>
        (Task)job.Method.Invoke(services.GetRequiredService(job.Type), job.Args.ToArray())!;

    private sealed class FixedClock(DateTime nowAtUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(nowAtUtc);
    }
}
