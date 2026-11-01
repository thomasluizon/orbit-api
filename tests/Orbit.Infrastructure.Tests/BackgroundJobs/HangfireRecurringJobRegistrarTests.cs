using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orbit.Infrastructure.BackgroundJobs;
using System.Reflection;

namespace Orbit.Infrastructure.Tests.BackgroundJobs;

public class HangfireRecurringJobRegistrarTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConversationRetentionIsNotRegisteredInEitherSchedulerMode(bool durable)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=orbit;Username=orbit";
        builder.Configuration["BackgroundServices:UseDurableQueue"] = durable.ToString();

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
        var registrar = new HangfireRecurringJobRegistrar(manager, [],
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HangfireRecurringJobRegistrar>.Instance);
        await registrar.StartAsync(default);
        NSubstitute.SubstituteExtensions.Received(manager, 1).RemoveIfExists("conversation-retention");
    }

}
