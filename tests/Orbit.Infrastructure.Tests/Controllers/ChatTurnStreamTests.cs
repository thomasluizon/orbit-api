using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.Extensions;
using Orbit.Api.Hosting;
using Orbit.Api.Idempotency;
using Orbit.Api.Middleware;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Controllers;

public sealed class ChatTurnStreamTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostAcknowledgement_ConcurrentRetryAndReplay_PersistAndChargeOnce(bool cancelledWrite)
    {
        var mediator = Substitute.For<IMediator>();
        var userId = Guid.NewGuid();
        using var services = new ChatTurnStreamTestServices(mediator, userId);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            Interlocked.Increment(ref executions);
            received.TrySetResult();
            await finish.Task.WaitAsync(call.Arg<CancellationToken>());
            var command = call.Arg<ProcessUserChatCommand>();
            await using var scope = services.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
            var user = await db.Users.SingleAsync();
            user.IncrementAiMessageCount(new DateOnly(2026, 1, 1));
            db.ConversationTurns.Add(ConversationTurn.Create(userId, command.TurnId!.Value, "user",
                DateTime.UtcNow, null, command.Message, "[]").Value);
            await db.SaveChangesAsync();
            await command.StreamSink!(ChatStreamEvent.Delta("Reply"));
            return Result.Success(new ChatResponse("Reply", []));
        });
        var first = CreateController(services, userId, "same-key", new BrokenStream(cancelledWrite));
        var original = first.ProcessChatStream("Hello", null, null, CancellationToken.None);
        await Task.WhenAny(original, received.Task).WaitAsync(TimeSpan.FromSeconds(5));
        if (original.IsCompleted)
            await original;
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = CreateController(services, userId, "same-key");
        var retry = second.ProcessChatStream("Hello", null, null, CancellationToken.None);
        await Task.Yield();
        retry.IsCompleted.Should().BeFalse();
        finish.TrySetResult();
        await Task.WhenAll(original, retry).WaitAsync(TimeSpan.FromSeconds(5));
        var replay = CreateController(services, userId, "same-key");
        await replay.ProcessChatStream("Hello", null, null, CancellationToken.None);

        executions.Should().Be(1);
        await using var read = services.Provider.CreateAsyncScope();
        var context = read.ServiceProvider.GetRequiredService<OrbitDbContext>();
        (await context.ChatTurns.CountAsync()).Should().Be(1);
        (await context.ConversationTurns.CountAsync()).Should().Be(1);
        (await context.Users.SingleAsync()).AiMessagesUsedToday.Should().Be(1);
        var events = Events(second);
        events[0].GetProperty("type").GetString().Should().Be("started");
        events[0].GetProperty("state").GetString().Should().Be("running");
        events[0].GetProperty("replayed").GetBoolean().Should().BeTrue();
        Events(replay)[0].GetProperty("state").GetString().Should().Be("completed");
        Events(replay)[1].GetRawText().Should().Be(events[1].GetRawText());
        events[1].GetRawText().Should().Be((await context.ChatTurns.SingleAsync()).TerminalEvent);
        output.WriteLine("Joined acknowledgement: " + events[0].GetRawText());
        output.WriteLine("Completed replay acknowledgement: " + Events(replay)[0].GetRawText());
        output.WriteLine("Persisted terminal event: " + events[1].GetRawText());
    }

    [Fact]
    public async Task ConflictingPayload_IsRejectedBeforeAcknowledgement()
    {
        var mediator = Substitute.For<IMediator>();
        var userId = Guid.NewGuid();
        using var services = new ChatTurnStreamTestServices(mediator, userId);
        mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ChatResponse("Reply", [])));
        await CreateController(services, userId, "key").ProcessChatStream("Hello", null, null, CancellationToken.None);
        var conflict = CreateController(services, userId, "key");

        var result = await conflict.ProcessChatStream("Changed", null, null, CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        conflict.Response.Body.Length.Should().Be(0);
        await mediator.Received(1).Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImageBytes_AreReplayedAndConflictsRejected()
    {
        var mediator = Substitute.For<IMediator>();
        var userId = Guid.NewGuid();
        using var services = new ChatTurnStreamTestServices(mediator, userId);
        mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ChatResponse("Reply", [])));
        var stream = services.Provider.GetRequiredService<ChatTurnStream>();
        var image = new ProcessUserChatCommand(userId, "Image", [1, 2, 3], "image/png");
        await stream.RunAsync(image, CreateController(services, userId, "image").HttpContext, "image", default);
        await stream.RunAsync(image, CreateController(services, userId, "image").HttpContext, "image", default);
        var conflict = await stream.RunAsync(image with { ImageData = [4] },
            CreateController(services, userId, "image").HttpContext, "image", default);
        conflict.Should().BeOfType<ConflictObjectResult>();
        await mediator.Received(1).Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BlockAnswer_ReplaysFinalWithoutDispatchingOrChargingAgain()
    {
        var mediator = Substitute.For<IMediator>();
        var userId = Guid.NewGuid();
        using var services = new ChatTurnStreamTestServices(mediator, userId);
        mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<ProcessUserChatCommand>().BlockAnswer.Should().NotBeNull();
                call.Arg<ProcessUserChatCommand>().StreamSink.Should().BeNull();
                return Result.Success(new ChatResponse("Answer accepted", []));
            });
        var answer = JsonSerializer.Serialize(new AstraBlockAnswer(Guid.NewGuid(), ["choice"]));
        var first = CreateController(services, userId, "answer");
        await first.ProcessChatStream("Answer", null, null, default, blockAnswer: answer);
        var replay = CreateController(services, userId, "answer");
        await replay.ProcessChatStream("Answer", null, null, default, blockAnswer: answer);
        Events(replay).Last().GetRawText().Should().Be(Events(first).Last().GetRawText());
        var conflict = await CreateController(services, userId, "answer").ProcessChatStream("Answer", null, null,
            default, blockAnswer: JsonSerializer.Serialize(new AstraBlockAnswer(Guid.NewGuid(), ["other"])));
        conflict.Should().BeOfType<ConflictObjectResult>();
        await mediator.Received(1).Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisconnectDuringExecution_DoesNotCancelTurn()
    {
        var mediator = Substitute.For<IMediator>();
        var userId = Guid.NewGuid();
        using var services = new ChatTurnStreamTestServices(mediator, userId);
        using var disconnect = new CancellationTokenSource();
        mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await disconnect.CancelAsync();
            call.Arg<CancellationToken>().IsCancellationRequested.Should().BeFalse();
            await call.Arg<ProcessUserChatCommand>().StreamSink!(ChatStreamEvent.Delta("Reply"));
            return Result.Success(new ChatResponse("Reply", []));
        });
        await CreateController(services, userId, "key").ProcessChatStream("Hello", null, null, disconnect.Token);
        var retry = CreateController(services, userId, "key");
        await retry.ProcessChatStream("Hello", null, null, default);

        Events(retry).Last().GetProperty("type").GetString().Should().Be("final");
        await mediator.Received(1).Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AbandonedReservation_ExpiredKeyNeverExecutesAgain()
    {
        var mediator = Substitute.For<IMediator>();
        var userId = Guid.NewGuid();
        using var services = new ChatTurnStreamTestServices(mediator, userId);
        var command = new ProcessUserChatCommand(userId, "Hello");
        var candidate = ChatTurn.Create(userId, ChatTurnIdentity.HashKey("abandoned"),
            ChatTurnIdentity.Fingerprint(command), DateTime.UtcNow.AddSeconds(-1));
        await services.Provider.GetRequiredService<IChatTurnStore>().ClaimAsync(candidate, default);
        var controller = CreateController(services, userId, "abandoned");

        await services.Provider.GetRequiredService<ChatTurnStream>()
            .RunAsync(command, controller.HttpContext, "abandoned", default);

        Events(controller).Last().GetProperty("code").GetString().Should().Be("CHAT_TURN_INTERRUPTED");
        Events(controller).Last().GetProperty("turnId").GetGuid().Should().Be(candidate.Id);
        await mediator.DidNotReceiveWithAnyArgs().Send(default(ProcessUserChatCommand)!, default);
    }

    [Fact]
    public async Task DrainExpiry_StoresInterruptedOutcome_AndNeverRedispatches()
    {
        var mediator = Substitute.For<IMediator>();
        var userId = Guid.NewGuid();
        using var services = new ChatTurnStreamTestServices(mediator, userId);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
            return Result.Success(new ChatResponse("Reply", []));
        });
        var controller = CreateController(services, userId, "shutdown");
        var drain = services.Provider.GetRequiredService<ShutdownDrain>();
        var middleware = new ShutdownDrainMiddleware(async _ =>
            await controller.ProcessChatStream("Hello", null, null, controller.HttpContext.RequestAborted));
        var request = middleware.InvokeAsync(controller.HttpContext, drain);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        services.Provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ShutdownDrainOptions>>()
            .Value.Timeout = TimeSpan.FromMilliseconds(100);

        await drain.StoppingAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        await request;
        var retry = CreateController(services, userId, "shutdown");
        await retry.ProcessChatStream("Hello", null, null, default);

        Events(retry).Last().GetProperty("code").GetString().Should().Be("CHAT_TURN_INTERRUPTED");
        await mediator.Received(1).Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>());
    }

    internal static ChatController CreateController(ChatTurnStreamTestServices services, Guid userId, string key, Stream? body = null)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = services.Provider,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"))
        };
        http.Request.Headers["Idempotency-Key"] = key;
        http.Response.Body = body ?? new MemoryStream();
        return new ChatController(services.Mediator, Substitute.For<IImageValidationService>(), NullLogger<ChatController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    internal static JsonElement[] Events(ChatController controller) => Encoding.UTF8
        .GetString(((MemoryStream)controller.Response.Body).ToArray()).Split('\n')
        .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
        .Select(line => JsonSerializer.Deserialize<JsonElement>(line[6..])).ToArray();

    private sealed class BrokenStream(bool cancelled) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(cancelled ? new OperationCanceledException("Acknowledgement lost")
                : new IOException("Acknowledgement lost"));
    }
}

internal sealed class ChatTurnStreamTestServices : IDisposable
{
    private readonly SqliteOrbitDbContextFactory _database = new();
    internal ServiceProvider Provider { get; }
    internal IMediator Mediator { get; }

    internal ChatTurnStreamTestServices(IMediator mediator, Guid userId)
    {
        Mediator = mediator;
        var user = User.Create("Chat User", "chat@example.com").Value;
        _database.Context.Entry(user).Property(value => value.Id).CurrentValue = userId;
        _database.Context.Users.Add(user);
        _database.Context.SaveChanges();
        var limits = Substitute.For<IDistributedRateLimitService>();
        limits.TryAcquireLeaseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(true, 1, 1, DateTime.UtcNow.AddMinutes(5)));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IHostApplicationLifetime>());
        services.AddOrbitShutdownDrain();
        services.AddSingleton(mediator);
        services.AddSingleton(limits);
        services.AddScoped(_ => _database.CreateContext());
        services.AddSingleton<IChatTurnStore, ChatTurnStore>();
        services.AddScoped<ChatTurnStream>();
        Provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        Provider.Dispose();
        _database.Dispose();
    }
}
