#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public partial class AgentChatWriteHoldTests
{
    [Theory]
    [InlineData("make it a checklist item instead", "checklist_item")]
    [InlineData("make it a sub-habit instead", "sub_habit")]
    [InlineData("make it a separate habit instead", "new_parent")]
    public async Task PlacementSentence_WithRealCompletionClientNeverDispatchesACompletion(string message, string selected)
    {
        var today = new DateOnly(2026, 10, 1);
        var parent = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, "Morning routine",
            FrequencyUnit.Day, 1, today, ChecklistItems: [new("Coffee", true)])).Value;
        _dbContext.Habits.Add(parent);
        await _dbContext.SaveChangesAsync();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var tools = new AiToolRegistry([new CreateSubHabitTool(mediator)]);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(today);
        var previewer = new PendingOperationChangePreviewer(new GenericRepository<Habit>(_dbContext),
            new GenericRepository<Goal>(_dbContext), new GenericRepository<Tag>(_dbContext), dates, tools);
        var executor = CreateExecutor(tools, previewer);
        var held = await executor.ExecuteAsync(new(_userId, "create_sub_habit",
            JsonSerializer.SerializeToElement(new
            {
                title = "Stretch",
                parent_habit_id = parent.Id,
                frequency_unit = "Day",
                due_time = "09:00",
                reminder_enabled = true,
                reminder_times = new[] { 15 }
            }),
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, IncludeChangePreview: true));
        held.PendingOperation.Should().NotBeNull();
        var pending = held.PendingOperation!;
        using var transport = new RejectCompletionTransport();
        using var http = new HttpClient(transport);
        var usage = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(new ResponsesClient(new ApiKeyCredential("test-key"), new ResponsesClientOptions
        {
            Endpoint = new Uri("https://orbit.test/v1"),
            Transport = new HttpClientPipelineTransport(http)
        }), NullLogger<AiCompletionClient>.Instance, usage);
        var budget = Substitute.For<IAiDailyBudget>();
        budget.ReserveAsync(_userId, Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        var completion = new BudgetedAiCompletionClient(client, budget, new Microsoft.AspNetCore.Http.HttpContextAccessor(),
            NullLogger<BudgetedAiCompletionClient>.Instance, Substitute.For<IAiRequestCostEstimator>());
        var intent = new AiIntentService(completion, usage, NullLogger<AiIntentService>.Instance);
        var syncer = Substitute.For<IGoalProgressReadSyncer>();
        syncer.ComputeFreshValuesAsync(_userId, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, int>());
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());
        var handler = new ProcessUserChatCommandHandler(
            new(new GenericRepository<Habit>(_dbContext), new GenericRepository<Goal>(_dbContext),
                new GenericRepository<User>(_dbContext), new GenericRepository<Tag>(_dbContext),
                new GenericRepository<ChecklistTemplate>(_dbContext), flags),
            new(intent, tools, Substitute.For<ISystemPromptBuilder>(), _catalogService),
            new(dates, Substitute.For<IUserStreakService>(), Substitute.For<IPayGateService>(),
                Substitute.For<IUnitOfWork>(), Substitute.For<IServiceScopeFactory>(), executor,
                Substitute.For<IPendingClarificationStore>(), syncer, Substitute.For<IGamificationService>(), mediator,
                Substitute.For<IProductAnalytics>(), Substitute.For<IHabitScheduleLogReader>(),
                Substitute.For<IIssuedAstraBlockStore>(), _pendingOperationStore, previewer),
            NullLogger<ProcessUserChatCommandHandler>.Instance, Substitute.For<IAppConfigService>(), ModerationTestDouble.Clean());

        var response = await handler.Handle(new(_userId, message, ClientContext: new AgentClientContext(SupportsParts: true)), default);

        response.IsSuccess.Should().BeTrue(response.Error);
        var revised = response.Value.PendingOperations.Should().ContainSingle().Which;
        revised.Id.Should().Be(pending.Id);
        revised.Items!.Single().Placement!.Selected.Should().Be(selected);
        transport.Calls.Should().Be(0);
        await budget.DidNotReceiveWithAnyArgs().ReserveAsync(default, default, default);
        var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        stored.Arguments.GetProperty("due_time").GetString().Should().Be("09:00");
        stored.Arguments.GetProperty("reminder_times")[0].GetInt32().Should().Be(15);
        parent.ChecklistItems.Should().ContainSingle();
    }

    private sealed class RejectCompletionTransport : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Placement switching must not call the completion provider.");
        }
    }
}
