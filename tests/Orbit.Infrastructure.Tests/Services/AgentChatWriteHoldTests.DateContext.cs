using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public partial class AgentChatWriteHoldTests
{
    [Theory]
    [InlineData("en", "pending")]
    [InlineData("pt-BR", "pending")]
    [InlineData("en", "approved")]
    [InlineData("pt-BR", "edited")]
    [InlineData("pt-BR", "rejected")]
    [InlineData("en", "expired")]
    public async Task ReservationReplay_PendingOperationsSupplyOnlyThisConversationsLastPreview(string language, string outcome)
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "AstraDates", language + ".json")));
        var script = fixture.RootElement;
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 11, 1));
        var user = await _dbContext.Users.SingleAsync(item => item.Id == _userId);
        user.SetLanguage(language);
        user.SetTimeZone("America/Sao_Paulo");
        await _dbContext.SaveChangesAsync();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var registry = new AiToolRegistry([new CreateHabitTool(dates, mediator)]);
        var habits = new GenericRepository<Habit>(_dbContext);
        var goals = new GenericRepository<Goal>(_dbContext);
        var tags = new GenericRepository<Tag>(_dbContext);
        var previewer = new PendingOperationChangePreviewer(habits, goals, tags, dates, registry);
        var work = Substitute.For<IUnitOfWork>();
        work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        var payGate = Substitute.For<IPayGateService>();
        payGate.TryConsumeAiMessage(_userId, work, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var progress = Substitute.For<IGoalProgressReadSyncer>();
        progress.ComputeFreshValuesAsync(_userId, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        var model = Substitute.For<IAiIntentService>();
        var requests = new List<AiToolRequest>();
        model.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                requests.Add(call.Arg<AiToolRequest>());
                return Result.Success(requests.Count == 1
                    ? new AiResponse
                    {
                        ToolCalls = [new("create_habit", "reservation", script.GetProperty("create_arguments"))],
                        ConversationContext = new AiConversationContext { Messages = new List<object>(), Options = new object() }
                    }
                    : new AiResponse { TextMessage = script.GetProperty("follow_up_answer").GetString() });
            });
        model.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse()));
        var handler = new ProcessUserChatCommandHandler(
            new ChatDataDependencies(habits, goals, new GenericRepository<User>(_dbContext), tags,
                new GenericRepository<ChecklistTemplate>(_dbContext), Substitute.For<IFeatureFlagService>()),
            new ChatAiDependencies(model, registry, Substitute.For<ISystemPromptBuilder>(), _catalogService),
            new ChatExecutionDependencies(dates, Substitute.For<IUserStreakService>(), payGate, work,
                Substitute.For<IServiceScopeFactory>(), CreateExecutor(registry, previewer), Substitute.For<IPendingClarificationStore>(),
                progress, Substitute.For<IGamificationService>(), mediator, Substitute.For<IProductAnalytics>(),
                Substitute.For<IHabitScheduleLogReader>(), Substitute.For<IIssuedAstraBlockStore>(), _pendingOperationStore, previewer),
            NullLogger<ProcessUserChatCommandHandler>.Instance, Substitute.For<IAppConfigService>(), ModerationTestDouble.Clean());
        var client = new AgentClientContext(ConversationId: Guid.NewGuid(), SupportsParts: true,
            SupportsPendingOperationChanges: true, Locale: language);
        var first = await handler.Handle(new(_userId, script.GetProperty("first_message").GetString()!,
            ImageData: [137, 80, 78, 71], ImageMimeType: "image/png", ClientContext: client), default);
        first.IsSuccess.Should().BeTrue(first.Error);
        var pending = first.Value.PendingOperations.Should().ContainSingle(JsonSerializer.Serialize(first.Value)).Which;
        var state = await _dbContext.PendingAgentOperations.SingleAsync(item => item.Id == pending.Id);
        if (outcome == "edited")
        {
            var revised = state.ArgumentsJson.Replace("20:30", "21:00", StringComparison.Ordinal);
            state.Revise(revised, AgentOperationFingerprint.Compute(state.OperationId, revised), "edited-preview", DateTime.UtcNow)
                .Should().BeTrue();
        }
        if (outcome == "approved") { state.SetConfirmationTokenHash("approval"); state.MarkConsumed(); }
        if (outcome == "rejected") state.Cancel(DateTime.UtcNow).Should().BeTrue();
        if (outcome == "expired") _dbContext.Entry(state).Property(item => item.ExpiresAtUtc).CurrentValue = DateTime.UtcNow.AddMinutes(-1);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var followUp = script.GetProperty("follow_up").GetString()!;
        var second = await handler.Handle(new(_userId, followUp, ClientContext: client), default);
        second.IsSuccess.Should().BeTrue(second.Error);
        second.Value.AiMessage.Should().Be(script.GetProperty("follow_up_answer").GetString());
        second.Value.Parts!.OfType<ChatBlockPart>().Should().BeEmpty();
        requests[1].DynamicContext.Should().ContainAll("conversation_last_previews", pending.Id.ToString(), "2026-12-31",
            outcome == "edited" ? "21:00" : "20:30", "\"Outcome\":\"" + outcome + "\"");
        pending.Items.Should().ContainSingle().Which.Fields.Should().Contain(field => field.Field == "due_date"
            && field.NewValue == "2026-12-31" && field.Provenance == "requested" && field.IsEditable && field.ValueType == "date");
        requests[1].AccountContext.Should().Contain("America/Sao_Paulo");
        requests[1].History.Should().BeNullOrEmpty();
        requests[1].ImageData.Should().BeNull();

        var other = await handler.Handle(new(_userId, followUp, ClientContext: client with { ConversationId = Guid.NewGuid() }), default);
        other.IsSuccess.Should().BeTrue(other.Error);
        requests[2].DynamicContext.Should().NotContain("conversation_last_previews");
    }
}
