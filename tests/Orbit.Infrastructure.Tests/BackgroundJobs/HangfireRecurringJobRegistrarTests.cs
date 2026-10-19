using Hangfire;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Infrastructure.BackgroundJobs;
using System.Reflection;

namespace Orbit.Infrastructure.Tests.BackgroundJobs;

public class HangfireRecurringJobRegistrarTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScheduleCleanupIsRegisteredInBothSchedulerModes(bool durable)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=orbit;Username=orbit";
        builder.Configuration["BackgroundServices:UseDurableQueue"] = durable.ToString();

        typeof(Orbit.Api.Extensions.ServiceCollectionExtensions)
            .GetMethod("AddBackgroundServices", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [builder]);

        builder.Services.Should().ContainSingle(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(HangfireRecurringJobRegistrar));
    }

    [Fact]
    public async Task StartAsync_RemovesPersistedMemorySchedule()
    {
        var manager = Substitute.For<IRecurringJobManager>();
        var registrar = new HangfireRecurringJobRegistrar(manager, [],
            NullLogger<HangfireRecurringJobRegistrar>.Instance);

        await registrar.StartAsync(CancellationToken.None);

        manager.Received(1).RemoveIfExists("openai-batch-poller");
    }
}
