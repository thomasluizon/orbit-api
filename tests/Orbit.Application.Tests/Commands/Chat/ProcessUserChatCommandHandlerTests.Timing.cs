using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    private TimeProvider? _turnClock;

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task Handle_Timing_RecordsReadWriteAndFailedToolTurns(bool readOnly, bool stream, bool failTool)
    {
        SetupUserAndPayGate();
        var clock = new ChatTimingClock();
        _turnClock = clock;
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new[] { FeatureFlagKeys.AstraEvalTrace });
        _moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            clock.Advance(1);
            return new ModerationResult(false, false, []);
        });
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            clock.Advance(2);
            return Today;
        });
        _promptBuilder.BuildStatic(Arg.Any<PromptBuildRequest>()).Returns(_ =>
        {
            clock.Advance(3);
            return "private prompt";
        });
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            clock.Advance(5);
            return 1;
        });
        var tool = ReadTool(readOnly ? "query_habits" : "update_profile_preferences", new ToolResult(true));
        tool.IsReadOnly.Returns(readOnly);
        tool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await Task.Yield();
            clock.Advance(7);
            if (failTool)
                throw new InvalidOperationException("private tool failure");
            return new ToolResult(true, EntityName: "private habit");
        });
        var completion = CreateTimedCompletion(clock);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(),
            Arg.Any<CancellationToken>()).Returns(_ => CompleteAsync(ToolResponse(tool.Name, "call", "{}")));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => CompleteAsync(new AiResponse { TextMessage = "Done." }));

        var result = await CreateHandler(tool).Handle(new ProcessUserChatCommand(UserId,
            "private message private@example.com", EvalTraceRequested: true,
            StreamSink: stream ? _ => Task.CompletedTask : null), default);

        result.IsSuccess.Should().BeTrue();
        var timing = result.Value.EvalTrace!.Timing!;
        var names = new List<string> { "moderation", "context_load", "context_load", "prompt_build",
            "budget", "budget", "model_call", "budget", "tool_call" };
        var milliseconds = new List<double> { 1, 2, 0, 3, 11, 0, 13, 0, 7 };
        if (!failTool)
        {
            names.AddRange(["budget", "budget", "model_call", "budget"]);
            milliseconds.AddRange([11, 0, 13, 0]);
        }
        names.AddRange(["save", "composition", "other"]);
        milliseconds.AddRange([5, 0, 0]);
        timing.Phases.Select(phase => phase.Name).Should().Equal(names);
        timing.Phases.Select(phase => phase.Milliseconds).Should().Equal(milliseconds);
        timing.TotalMilliseconds.Should().Be(failTool ? 42 : 66);
        timing.Phases.Sum(phase => phase.Milliseconds).Should().Be(timing.TotalMilliseconds);
        result.Value.EvalTrace.ToolCalls.Single().Status.Should().Be(failTool ? "Failed" : "Succeeded");
        AssertTimingLog(timing);

        Task<Result<AiResponse>> CompleteAsync(AiResponse response) => completion.RunAsync(UserId, async _ =>
        {
            await Task.Yield();
            clock.Advance(13);
            return Result.Success(response);
        }, default, "{}");
    }

    [Fact]
    public async Task Handle_Timing_LogsTotalForModerationFailureWithoutEvalOptIn()
    {
        var clock = new ChatTimingClock();
        _turnClock = clock;
        _moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<ModerationResult>(_ =>
        {
            clock.Advance(17);
            throw new InvalidOperationException("private moderation failure");
        });

        var action = () => CreateHandler().Handle(new ProcessUserChatCommand(UserId, "private message"), default);

        await action.Should().ThrowAsync<InvalidOperationException>();
        var receipt = TimingLog();
        receipt["TotalMilliseconds"].Should().Be(17d);
        receipt["Phases"]!.ToString().Should().Contain("moderation").And.NotContain("private");
    }

    private static BudgetedAiCompletionClient CreateTimedCompletion(ChatTimingClock clock)
    {
        var estimates = Substitute.For<IAiRequestCostEstimator>();
        estimates.EstimateMicroDollarsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            clock.Advance(11);
            return 100L;
        });
        var inner = new AiCompletionClient(Options.Create(new AiSettings { ApiKey = "fake-test-key" }),
            NullLogger<AiCompletionClient>.Instance, Substitute.For<IAiUsageRecorder>());
        return new BudgetedAiCompletionClient(inner, Substitute.For<IAiDailyBudget>(), new HttpContextAccessor(),
            NullLogger<BudgetedAiCompletionClient>.Instance, estimates);
    }

    private Dictionary<string, object?> TimingLog() => _logger.ReceivedCalls()
        .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log))
        .Select(call => (IEnumerable<KeyValuePair<string, object?>>)call.GetArguments()[2]!)
        .Where(state => state.Any(pair => pair.Key == "TotalMilliseconds"))
        .Should().ContainSingle().Which.ToDictionary(pair => pair.Key, pair => pair.Value);

    private void AssertTimingLog(ChatTurnTiming timing)
    {
        var receipt = TimingLog();
        receipt["TotalMilliseconds"].Should().Be(timing.TotalMilliseconds);
        receipt["Phases"].Should().Be(JsonSerializer.Serialize(timing.Phases, ChatStreamEvent.SerializerOptions));
        receipt.Keys.Should().BeEquivalentTo("Phases", "TotalMilliseconds", "{OriginalFormat}");
        JsonSerializer.Serialize(receipt).Should().NotContain("private").And.NotContain("@");
    }
}
