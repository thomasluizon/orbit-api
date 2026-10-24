using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.Extensions;
using Orbit.Api.Hosting;
using Orbit.Api.Middleware;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;
using Orbit.Api.Idempotency;

namespace Orbit.Infrastructure.Tests.Hosting;

[CollectionDefinition("ShutdownSignals", DisableParallelization = true)]
public sealed class ShutdownSignalsCollection;

[Collection("ShutdownSignals")]
public sealed class ShutdownDrainTests
{
    [Fact]
    public void Host_ShutdownBudget_LeavesTimeBeforeRenderKill()
    {
        using var fixture = new StreamHost();
        fixture.Host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout
            .Should().Be(TimeSpan.FromSeconds(270));
        fixture.Host.Services.GetRequiredService<IOptions<ShutdownDrainOptions>>().Value.Timeout
            .Should().Be(TimeSpan.FromSeconds(240));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sigterm_AcknowledgedReplyLongerThanDefaultShutdown_ReachesFinal(bool keyed)
    {
        if (OperatingSystem.IsWindows())
            return;

        using var fixture = new StreamHost(keyed: keyed);
        await fixture.Host.StartAsync();
        var request = fixture.StartStream();
        await fixture.ProcessingStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.EventTypes().Should().Equal("started");
        var stopped = fixture.Host.WaitForShutdownAsync();
        try
        {
            using var signal = Process.Start(new ProcessStartInfo("/bin/kill")
            {
                ArgumentList = { "-TERM", Environment.ProcessId.ToString() },
                UseShellExecute = false
            })!;
            await signal.WaitForExitAsync();
            signal.ExitCode.Should().Be(0);
            await fixture.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromSeconds(31));
            stopped.IsCompleted.Should().BeFalse("the acknowledged reply is still active after the default 30 seconds");
            fixture.FinishReply.TrySetResult();
            await request.WaitAsync(TimeSpan.FromSeconds(10));
            await stopped.WaitAsync(TimeSpan.FromSeconds(10));
            fixture.EventTypes().Should().Equal("started", "delta", "final");
            await fixture.Mediator.Received(1).Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            fixture.FinishReply.TrySetResult();
            await request.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.Host.StopAsync();
        }
    }

    [Fact]
    public async Task Shutdown_RejectsNewWorkWithoutAcknowledgingOrDispatching()
    {
        using var fixture = new StreamHost();
        await fixture.Host.StartAsync();
        var active = fixture.StartStream();
        await fixture.ProcessingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = fixture.Host.StopAsync();
        using var body = new MemoryStream();
        var lateRequest = new DefaultHttpContext();
        lateRequest.Response.Body = body;

        await fixture.StartStream(lateRequest).WaitAsync(TimeSpan.FromSeconds(5));

        lateRequest.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.Length.Should().Be(0);
        stopped.IsCompleted.Should().BeFalse();
        await fixture.Mediator.Received(1).Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>());
        fixture.FinishReply.TrySetResult();
        await active.WaitAsync(TimeSpan.FromSeconds(5));
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.EventTypes().Should().Equal("started", "delta", "final");
    }

    [Fact]
    public async Task DrainDeadline_CancelsUnfinishedReplyWithoutFinalOrReplay()
    {
        using var fixture = new StreamHost(TimeSpan.FromMilliseconds(100));
        await fixture.Host.StartAsync();
        var request = fixture.StartStream();
        await fixture.ProcessingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await fixture.Host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await request.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.CommandCancelled.Should().BeTrue();
        fixture.Http.RequestAborted.IsCancellationRequested.Should().BeFalse();
        fixture.EventTypes().Should().Equal("started");
        await fixture.Mediator.Received(1).Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HostDeadline_ShorterThanDrain_CancelsUnfinishedReply()
    {
        using var fixture = new StreamHost();
        await fixture.Host.StartAsync();
        var request = fixture.StartStream();
        await fixture.ProcessingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await fixture.Host.StopAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(5));
        await request.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.CommandCancelled.Should().BeTrue();
        fixture.EventTypes().Should().Equal("started");
    }

    [Fact]
    public async Task ClientDisconnect_CancelsCommandWithoutFinalOrReplay()
    {
        using var fixture = new StreamHost();
        await fixture.Host.StartAsync();
        using var disconnect = new CancellationTokenSource();
        fixture.Http.RequestAborted = disconnect.Token;
        var request = fixture.StartStream();
        await fixture.ProcessingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await disconnect.CancelAsync();
        await request.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.CommandCancelled.Should().BeTrue();
        fixture.Http.RequestAborted.Should().Be(disconnect.Token);
        fixture.EventTypes().Should().Equal("started");
        await fixture.Mediator.Received(1).Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>());
        await fixture.Host.StopAsync();
    }

    private sealed class StreamHost : IDisposable
    {
        private readonly MemoryStream _body = new();
        private readonly SqliteOrbitDbContextFactory? _database;
        private readonly RequestDelegate _pipeline;
        public IHost Host { get; }
        public IMediator Mediator { get; } = Substitute.For<IMediator>();
        public DefaultHttpContext Http { get; } = new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "Test"))
        };
        public TaskCompletionSource ProcessingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CommandCancelled { get; private set; }

        public StreamHost(TimeSpan? drainTimeout = null, bool keyed = false)
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Services.AddOrbitShutdownDrain();
            if (keyed)
            {
                _database = new SqliteOrbitDbContextFactory();
                var user = User.Create("Stream User", "stream@example.com").Value;
                _database.Context.Entry(user).Property(value => value.Id).CurrentValue = Guid.Parse(Http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
                _database.Context.Users.Add(user);
                _database.Context.SaveChanges();
                builder.Services.AddScoped(_ => _database.CreateContext());
                builder.Services.AddSingleton<IChatTurnStore, ChatTurnStore>();
                builder.Services.AddSingleton(Mediator);
                builder.Services.AddScoped<ChatTurnStream>();
                var limits = Substitute.For<IDistributedRateLimitService>();
                limits.TryAcquireLeaseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                    .Returns(new DistributedRateLimitDecision(true, 1, 1, DateTime.UtcNow.AddMinutes(5)));
                builder.Services.AddSingleton(limits);
                Http.Request.Headers["Idempotency-Key"] = "sigterm-turn";
            }
            if (drainTimeout is { } timeout)
                builder.Services.Configure<ShutdownDrainOptions>(options => options.Timeout = timeout);
            Host = builder.Build();
            Http.RequestServices = Host.Services;
            Host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() => Stopping.TrySetResult());
            Http.Response.Body = _body;
            Mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    ProcessingStarted.TrySetResult();
                    try
                    {
                        await FinishReply.Task.WaitAsync(call.Arg<CancellationToken>());
                        await call.Arg<ProcessUserChatCommand>().StreamSink!(ChatStreamEvent.Delta("Reply"));
                        return Result.Success(new ChatResponse("Reply", []));
                    }
                    catch (OperationCanceledException)
                    {
                        CommandCancelled = true;
                        throw;
                    }
                });
            var middleware = new ShutdownDrainMiddleware(async context =>
            {
                var controller = new ChatController(Mediator, Substitute.For<IImageValidationService>(), NullLogger<ChatController>.Instance)
                {
                    ControllerContext = new ControllerContext { HttpContext = context }
                };
                await controller.ProcessChatStream("Hello", null, null, context.RequestAborted);
            });
            _pipeline = context => middleware.InvokeAsync(context, Host.Services.GetRequiredService<ShutdownDrain>());
        }

        public Task StartStream(DefaultHttpContext? context = null) => _pipeline(context ?? Http);

        public string?[] EventTypes() => System.Text.Encoding.UTF8.GetString(_body.ToArray())
            .Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line[6..]).GetProperty("type").GetString()).ToArray();

        public void Dispose()
        {
            FinishReply.TrySetResult();
            Host.Dispose();
            _body.Dispose();
            _database?.Dispose();
        }
    }
}
