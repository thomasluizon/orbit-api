using FluentAssertions;
using Microsoft.Extensions.Logging;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class SlowQueryCommandInterceptorTests
{
    [Fact]
    public void ThreeSlowCommands_InOneRequest_WriteOneSummaryWithCountThree()
    {
        var logger = new RecordingLogger<SlowQueryCommandInterceptor>();
        var interceptor = new SlowQueryCommandInterceptor(logger, new DatabaseConnectionSettings { SlowQueryThresholdMilliseconds = 100 });
        using (SlowQueryCommandInterceptor.BeginRequest())
            for (var i = 0; i < 3; i++) interceptor.LogIfSlow(TimeSpan.FromMilliseconds(250));
        logger.Entries.Should().ContainSingle().Which.Should().Be(LogLevel.Warning);
        logger.Messages.Should().ContainSingle().Which.Should().Contain("3 slow database commands");
        logger.Messages.Should().NotContain(message => message.Contains("SELECT", StringComparison.Ordinal));
    }

    [Fact]
    public void LogIfSlow_WhenDurationExceedsThreshold_LogsSingleWarning()
    {
        var logger = new RecordingLogger<SlowQueryCommandInterceptor>();
        var interceptor = new SlowQueryCommandInterceptor(
            logger, new DatabaseConnectionSettings { SlowQueryThresholdMilliseconds = 100 });

        interceptor.LogIfSlow(TimeSpan.FromMilliseconds(250));

        logger.Entries.Should().ContainSingle().Which.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public void LogIfSlow_WhenDurationBelowThreshold_DoesNotLog()
    {
        var logger = new RecordingLogger<SlowQueryCommandInterceptor>();
        var interceptor = new SlowQueryCommandInterceptor(
            logger, new DatabaseConnectionSettings { SlowQueryThresholdMilliseconds = 100 });

        interceptor.LogIfSlow(TimeSpan.FromMilliseconds(50));

        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void LogIfSlow_WhenDurationEqualsThreshold_LogsWarning()
    {
        var logger = new RecordingLogger<SlowQueryCommandInterceptor>();
        var interceptor = new SlowQueryCommandInterceptor(
            logger, new DatabaseConnectionSettings { SlowQueryThresholdMilliseconds = 100 });

        interceptor.LogIfSlow(TimeSpan.FromMilliseconds(100));

        logger.Entries.Should().ContainSingle();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogLevel> Entries { get; } = [];
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(logLevel);
            Messages.Add(formatter(state, exception));
        }
    }
}
