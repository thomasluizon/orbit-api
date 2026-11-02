using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orbit.Infrastructure.BackgroundJobs;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Services;

namespace Orbit.Infrastructure.Tests.BackgroundJobs;

public class HangfireRecurringJobRegistrarTests
{
    [Fact]
    public async Task ChatTurnRetention_PurgesOnlyOldIdempotencyRows()
    {
        using var fixture = new ConversationTurnRecorderTests.Fixture(enabled: false);
        var nowAtUtc = DateTime.UtcNow;
        var old = ChatTurn.Create(fixture.User.Id, new string('a', 64), new string('b', 64), nowAtUtc.AddDays(-31));
        var recent = ChatTurn.Create(fixture.User.Id, new string('c', 64), new string('d', 64), nowAtUtc.AddMinutes(-1));
        var unexpired = ChatTurn.Create(fixture.User.Id, new string('e', 64), new string('f', 64), nowAtUtc.AddDays(1));
        old.Complete("private terminal reply");
        fixture.Db.ChatTurns.AddRange(old, recent, unexpired);
        var retained = ConversationTurn.Create(fixture.User.Id, Guid.NewGuid(), "user", nowAtUtc.AddDays(-100),
            null, "Retained conversation", "[]", conversationId: fixture.ConversationId, turnOrder: 1).Value;
        fixture.Db.ConversationTurns.Add(retained);
        await fixture.Db.SaveChangesAsync();
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=orbit;Username=orbit";
        typeof(Orbit.Api.Extensions.ServiceCollectionExtensions)
            .GetMethod("AddBackgroundServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [builder]);
        builder.Services.AddScoped(_ => fixture.Database.CreateContext());
        builder.Services.AddScoped<IUnitOfWork>(scope => new UnitOfWork(scope.GetRequiredService<OrbitDbContext>(), new DatabaseConnectionSettings()));
        var registration = builder.Services.SingleOrDefault(descriptor => descriptor.ServiceType.Name == "ChatTurnRetentionJob");
        registration.Should().NotBeNull();
        using var provider = builder.Services.BuildServiceProvider();
        var job = (IScheduledJob)provider.GetRequiredService(registration!.ServiceType);
        job.Name.Should().Be("chat-turn-retention");
        job.CronExpression.Should().Be("15 * * * *");

        await job.RunAsync(default);

        (await fixture.Db.ChatTurns.AsNoTracking().Select(turn => turn.Id).ToListAsync())
            .Should().BeEquivalentTo([recent.Id, unexpired.Id]);
        (await fixture.Db.ConversationTurns.AsNoTracking().SingleAsync()).Id.Should().Be(retained.Id);
    }

    [Fact]
    public void ConversationRetentionIsNotRegistered()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=orbit;Username=orbit";

        typeof(Orbit.Api.Extensions.ServiceCollectionExtensions)
            .GetMethod("AddBackgroundServices", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [builder]);

        using var provider = builder.Services.BuildServiceProvider();
        builder.Services.Should().NotContain(descriptor => descriptor.ServiceType.Name == "ConversationRetentionJob");
        builder.Services.Should().ContainSingle(descriptor => descriptor.ServiceType == typeof(ScheduledJobRunner));
        builder.Services.Should().ContainSingle(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(HangfireRecurringJobRegistrar));
    }

    [Fact]
    public async Task Startup_RemovesObsoleteConversationRetentionSchedule()
    {
        var manager = NSubstitute.Substitute.For<Hangfire.IRecurringJobManager>();
        using var database = new Orbit.Infrastructure.Tests.Persistence.SqliteOrbitDbContextFactory();
        using var provider = new ServiceCollection().AddScoped(_ => database.CreateContext()).BuildServiceProvider();
        var registrar = new HangfireRecurringJobRegistrar(manager, [],
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HangfireRecurringJobRegistrar>.Instance,
            BackgroundServiceHealthCheckTests.Create(provider));
        await registrar.StartAsync(default);
        NSubstitute.SubstituteExtensions.Received(manager, 1).RemoveIfExists("conversation-retention");
    }

    [Fact]
    public async Task Startup_AddOrUpdateThrows_StartFails()
    {
        using var database = new Orbit.Infrastructure.Tests.Persistence.SqliteOrbitDbContextFactory();
        using var provider = new ServiceCollection().AddScoped(_ => database.CreateContext()).BuildServiceProvider();
        var manager = NSubstitute.Substitute.For<Hangfire.IRecurringJobManager>();
        var job = NSubstitute.Substitute.For<IScheduledJob>();
        NSubstitute.SubstituteExtensions.Returns(job.Name, "reminder-scheduler");
        NSubstitute.SubstituteExtensions.Returns(job.CronExpression, "* * * * *");
        NSubstitute.SubstituteExtensions.When(manager, m => m.AddOrUpdate(
            NSubstitute.Arg.Any<string>(), NSubstitute.Arg.Any<Hangfire.Common.Job>(),
            NSubstitute.Arg.Any<string>(), NSubstitute.Arg.Any<Hangfire.RecurringJobOptions>()))
            .Do(_ => throw new InvalidOperationException("registration failed"));
        var registrar = new HangfireRecurringJobRegistrar(manager, [job],
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HangfireRecurringJobRegistrar>.Instance,
            BackgroundServiceHealthCheckTests.Create(provider, null, job));
        var start = () => registrar.StartAsync(default);
        await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("registration failed");
    }

}
