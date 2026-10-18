using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Auth.Jobs;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class SignupJobDispatcherTests
{
    [Fact]
    public void Dispatch_RealHangfireStorageFailure_LogsRequestIdAndDoesNotThrow()
    {
        var storage = Substitute.For<JobStorage>();
        storage.GetConnection().Returns(_ => throw new InvalidOperationException("Connection unavailable"));
        var client = new BackgroundJobClient(storage);
        var create = () => client.Enqueue<CompleteSignupJob>(job => job.ExecuteAsync(Guid.Empty, true, "en", null));
        create.Should().Throw<BackgroundJobClientException>()
            .WithInnerException<InvalidOperationException>();
        var context = new DefaultHttpContext { TraceIdentifier = "signup-request" };
        var logger = Substitute.For<ILogger<SignupJobDispatcher>>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);
        var dispatcher = new SignupJobDispatcher(client, new HttpContextAccessor { HttpContext = context }, logger);

        var dispatch = () => dispatcher.Dispatch(Guid.NewGuid(), true, "pt-BR", "friend");

        dispatch.Should().NotThrow();
        var entry = logger.ReceivedCalls().Single(call => call.GetMethodInfo().Name == nameof(ILogger.Log));
        entry.GetArguments()[0].Should().Be(LogLevel.Warning);
        entry.GetArguments()[3].Should().BeOfType<BackgroundJobClientException>();
        var properties = (IReadOnlyList<KeyValuePair<string, object?>>)entry.GetArguments()[2]!;
        properties.Should().Contain(pair => pair.Key == "RequestId" && (string?)pair.Value == "signup-request");
    }

    [Fact]
    public void Dispatch_Success_EnqueuesOriginalInputs()
    {
        var client = Substitute.For<IBackgroundJobClient>();
        var dispatcher = new SignupJobDispatcher(client, new HttpContextAccessor(),
            Substitute.For<ILogger<SignupJobDispatcher>>());
        var userId = Guid.NewGuid();

        dispatcher.Dispatch(userId, true, "pt-BR", "friend");

        client.Received(1).Create(Arg.Is<Job>(job => job.Type == typeof(CompleteSignupJob)
            && (Guid)job.Args[0] == userId && (bool)job.Args[1]
            && (string)job.Args[2] == "pt-BR" && (string)job.Args[3] == "friend"), Arg.Any<IState>());
    }
}
