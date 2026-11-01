using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    private void ReplayGateEightLookup(string capture, Habit[] habits)
    {
        var readBlocks = new ReadBlockFactory(_userDateService, Substitute.For<IReadQueryTokenService>(), _userRepo);
        var readExecutor = Substitute.For<IAgentOperationExecutor>();
        readExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var request = call.Arg<AgentExecuteOperationRequest>();
                var ct = call.Arg<CancellationToken>();
                return await _executor.ExecuteAsync(request with
                {
                    Arguments = await readBlocks.ResolveAsync(request.OperationId, request.Arguments, request.UserId, ct)
                }, ct);
            });
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IAgentOperationExecutor)).Returns(readExecutor);
        services.GetService(typeof(IPendingClarificationStore)).Returns(_clarificationStore);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(services);
        _scopeFactory.CreateScope().Returns(scope);
        _userDateService.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(GateSevenToday);
        _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns([FeatureFlagKeys.AstraBlockFirst]);
        _scheduleLogReader.ReadDaysAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(),
            Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns([new HabitScheduleLogDay(habits[^1].Id, GateSevenToday, 0, 1, true)]);
        var calls = GateEightCapture(capture).GetProperty("toolCalls").EnumerateArray().Select((call, index) =>
        {
            var arguments = JsonNode.Parse(call.GetProperty("arguments").GetRawText())!.AsObject();
            var name = call.GetProperty("name").GetString()!;
            if (arguments["habit_id"]?.ToString() == "[redacted]")
                arguments["habit_id"] = (name == "update_habit" ? habits[0] : habits[^1]).Id.ToString();
            return new AiToolCall(name, "gate_eight_" + index, JsonSerializer.SerializeToElement(arguments));
        }).ToArray();
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ToolCalls = calls.Where(call => call.Name == "query_habits").ToArray(), ConversationContext = new() }));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ToolCalls = calls.Where(call => call.Name != "query_habits").ToArray(),
                ConversationContext = new()
            }));
    }

    private static string GateEightLookupMessage(string language, Habit[] habits) => language == "en"
        ? $"Rename Water ({habits[0].Id}) to Drink water, and skip Walk ({habits[^1].Id}) for today."
        : $"é, então, a Caminhada, id {habits[^1].Id}, hoje não vai dar não, pula só hoje tá, não apaga";

    [Theory]
    [InlineData("en", "compound-update-skip-en-r1", 2)]
    [InlineData("pt-BR", "voice-pt-r3", 1)]
    public async Task GateEight_CapturedWritesFollowSkippedLookupAndShareOnePreview(string language, string capture, int count)
    {
        var habits = GateSevenAccount(language);
        ReplayGateEightLookup(capture, habits);

        var result = await CreateHandler().Handle(new(_userId, GateEightLookupMessage(language, habits),
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, Locale: language)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.Parts.Should().BeNullOrEmpty();
        var pending = result.Value.PendingOperations.Should().ContainSingle().Which;
        pending.Items.Should().HaveCount(count);
        var skip = pending.Items!.Single(item => item.EntityId == habits[^1].Id);
        skip.Fields.Should().Contain(field => field.Field == "action" && field.ProposedValue!.Value.GetString() == "skip");
        skip.Fields.Should().Contain(field => field.Field == "date" && field.NewValue == "2026-10-28");
        var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        if (count == 2)
        {
            AccountChangeSet.Read(stored.Arguments)!.Select(member => member.OperationId).Should().Equal("update_habit", "skip_habit");
            pending.Items.Single(item => item.EntityId == habits[0].Id).Fields.Should()
                .Contain(field => field.Field == "title" && field.NewValue == "Drink water");
        }
        else
            stored.OperationId.Should().Be("skip_habit");
        var forwarded = _aiIntentService.ReceivedCalls().SelectMany(call => call.GetArguments())
            .OfType<IReadOnlyList<AiToolCallResult>>().Single();
        var skippedId = habits[^1].Id.ToString();
        forwarded.Select(result => result.Payload).OfType<HabitListCard>().SelectMany(card => card.Items)
            .Should().Contain(item => item.Id == skippedId && item.IsSkippedOnDate == true);
        await _mediator.DidNotReceive().Send(Arg.Any<SkipHabitCommand>(), Arg.Any<CancellationToken>());
        var sent = _aiIntentService.ReceivedCalls().SelectMany(call => call.GetArguments()).OfType<AiToolRequest>().Single();
        sent.SystemPrompt.Should().Contain("A read showing an occurrence as skipped does not fulfill a requested skip");
        var query = sent.ToolDeclarations.Select(declaration => JsonSerializer.SerializeToElement(declaration))
            .Single(declaration => declaration.GetProperty("name").GetString() == "query_habits");
        query.GetProperty("description").GetString().Should().Contain("even when isSkippedOnDate is true");
    }

    [Theory]
    [InlineData("en", "compound-update-skip-en-r3", 1)]
    [InlineData("pt-BR", "voice-pt-r2", 0)]
    public async Task GateEight_CapturedOmittedSkipNeverBecomesAnInventedWrite(string language, string capture, int count)
    {
        var habits = GateSevenAccount(language);
        ReplayGateEightLookup(capture, habits);

        var result = await CreateHandler().Handle(new(_userId, GateEightLookupMessage(language, habits),
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, Locale: language)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        (result.Value.PendingOperations ?? []).Should().HaveCount(count);
        (result.Value.PendingOperations ?? []).SelectMany(operation => operation.Items!).SelectMany(item => item.Fields)
            .Should().NotContain(field => field.Field == "action" && field.ProposedValue!.Value.GetString() == "skip");
        await _mediator.DidNotReceive().Send(Arg.Any<SkipHabitCommand>(), Arg.Any<CancellationToken>());
    }
}
