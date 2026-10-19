using System.Linq.Expressions;
using System.Text.Json;
using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Controllers;

public class ConversationRecordingStreamTests
{
    [Fact]
    public async Task Stream_RecorderThrows_StillEmitsReplyAndFinalEvent()
    {
        var user = User.Create("Person", "stream@example.com").Value;
        var users = Substitute.For<IGenericRepository<User>>();
        users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(new[] { FeatureFlagKeys.AstraConversationRecording });
        var habits = Substitute.For<IGenericRepository<Habit>>();
        habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Habit>());
        var goals = Substitute.For<IGenericRepository<Goal>>();
        goals.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Goal>());
        var dateService = Substitute.For<IUserDateService>();
        dateService.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 1, 1));
        var progress = Substitute.For<IGoalProgressReadSyncer>();
        progress.ComputeFreshValuesAsync(user.Id, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        var work = Substitute.For<IUnitOfWork>();
        work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        var payGate = Substitute.For<IPayGateService>();
        payGate.TryConsumeAiMessage(user.Id, work, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var intent = Substitute.For<IAiIntentService>();
        intent.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await call.Arg<Func<AiStreamEvent, Task>>()(AiStreamEvent.Delta("Your reply"));
                return Result.Success(new AiResponse { TextMessage = "Your reply" });
            });
        var recorder = Substitute.For<IConversationTurnRecorder>();
        recorder.TryRecord(Arg.Any<ConversationTurnCapture>()).Returns(_ => throw new InvalidOperationException("Forced failure"));
        var mediator = Substitute.For<IMediator>();
        var handler = new ProcessUserChatCommandHandler(
            new ChatDataDependencies(habits, goals, users, Substitute.For<IGenericRepository<Tag>>(),
                Substitute.For<IGenericRepository<ChecklistTemplate>>(), flags),
            new ChatAiDependencies(intent, new AiToolRegistry([]), Substitute.For<ISystemPromptBuilder>(), Substitute.For<IAgentCatalogService>()),
            new ChatExecutionDependencies(dateService, Substitute.For<IUserStreakService>(), payGate, work,
                Substitute.For<IServiceScopeFactory>(), Substitute.For<IAgentOperationExecutor>(), Substitute.For<IPendingClarificationStore>(),
                progress, Substitute.For<IGamificationService>(), mediator, Substitute.For<IProductAnalytics>(),
                Substitute.For<IHabitScheduleLogReader>(), Substitute.For<IIssuedAstraBlockStore>(), Substitute.For<IPendingAgentOperationStore>()),
            NullLogger<ProcessUserChatCommandHandler>.Instance, Substitute.For<IAppConfigService>(), recorder);
        mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<ProcessUserChatCommand>(), call.Arg<CancellationToken>()));
        using var body = new MemoryStream();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test"))
        };
        http.Response.Body = body;
        var controller = new ChatController(mediator, Substitute.For<IImageValidationService>(), NullLogger<ChatController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
        await controller.ProcessChatStream("Hello", null, null, CancellationToken.None);
        body.Position = 0;
        var stream = await new StreamReader(body).ReadToEndAsync();
        var events = stream.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line[6..])).ToArray();
        events.Select(item => item.GetProperty("type").GetString()).Should().Equal("started", "delta", "final");
        events[^1].GetProperty("response").GetProperty("aiMessage").GetString().Should().Be("Your reply");
        recorder.Received(1).TryRecord(Arg.Any<ConversationTurnCapture>());
    }
}
