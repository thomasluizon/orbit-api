using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Proactive.Services;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.AI;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class ProactiveEngineSchedulerServiceTests
{
    [Fact]
    public async Task ProactiveScheduler_HangfireRunThrows_Propagates()
    {
        var engine = Substitute.For<IProactiveEngine>();
        engine.EvaluateAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(new InvalidOperationException("tick failed")));
        using var provider = new ServiceCollection().AddSingleton(engine).BuildServiceProvider();
        var service = new ProactiveEngineSchedulerService(provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<ILogger<ProactiveEngineSchedulerService>>());
        var tick = () => service.RunAsync(default);
        await tick.Should().ThrowAsync<InvalidOperationException>().WithMessage("tick failed");
    }

    [Fact]
    public async Task ProactiveScheduler_HostedTickThrows_LogsAndRunsNextTick()
    {
        var engine = Substitute.For<IProactiveEngine>();
        var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        engine.EvaluateAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidOperationException("tick failed");
            next.TrySetResult();
            return Task.CompletedTask;
        });
        using var provider = new ServiceCollection().AddSingleton(engine).BuildServiceProvider();
        var logger = Substitute.For<ILogger<ProactiveEngineSchedulerService>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var clock = new CompletionTestClock();
        using var service = new ProactiveEngineSchedulerService(provider.GetRequiredService<IServiceScopeFactory>(), logger, clock);

        await service.StartAsync(default);
        try
        {
            for (var attempt = 0; attempt < 100 && clock.ActiveTimers == 0; attempt++) await Task.Delay(10);
            clock.ActiveTimers.Should().Be(1);
            clock.Advance(TimeSpan.FromHours(1));
            await next.Task.WaitAsync(TimeSpan.FromSeconds(10));
            logger.ReceivedCalls().Should().Contain(call => call.GetMethodInfo().Name == "Log"
                && (LogLevel)call.GetArguments()[0]! == LogLevel.Error);
        }
        finally { await service.StopAsync(default); }
    }
}
