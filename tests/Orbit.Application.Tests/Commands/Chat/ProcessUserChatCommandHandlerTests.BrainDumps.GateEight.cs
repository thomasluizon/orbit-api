using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", 3, false)]
    [InlineData("en", 3, true)]
    [InlineData("pt-BR", 2, true)]
    [InlineData("pt-BR", 3, false)]
    [InlineData("pt-BR", 3, true)]
    public async Task BrainDump_CapturedReadsStayInsideTheCombinedPreview(string language, int repeat, bool composition)
    {
        SetupUserAndPayGate();
        _logger.IsEnabled(Arg.Any<Microsoft.Extensions.Logging.LogLevel>()).Returns(true);
        if (composition) EnableComposition();
        var today = new DateOnly(2026, 10, 28);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(today);
        var existing = Habit.Create(new(HabitSchedule.Recurring, UserId,
            language == "en" ? "Water" : "Beber água", FrequencyUnit.Day, 1, today)).Value;
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { existing }.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToArray());
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { existing }.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToArray());
        using var fixture = BrainDumpCapture("reuse-" + language + "-r" + repeat, existing.Id);
        var calls = BrainDumpCalls(fixture);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(query, bulk);
        var held = HoldBrainDumpWithReads(bulk, query);
        var write = new AiResponse { ToolCalls = calls.Where(call => call.Name == bulk.Name).ToArray(), ConversationContext = new() };
        var reads = calls.Where(call => call.Name != bulk.Name).ToArray();
        SetupAiResponse(reads.Length == 0 ? write : new AiResponse { ToolCalls = reads, ConversationContext = new() });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Orbit.Domain.Common.Result.Success(write));

        var result = await handler.Handle(new(UserId, fixture.RootElement.GetProperty("message").GetString()!,
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true,
                SupportsHabitListCard: true, PartKinds: ["habitList"], Locale: language)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        _promptBuilder.Received().BuildStatic(Arg.Is<Orbit.Domain.Interfaces.PromptBuildRequest>(request => request.BlockFirstComposition == composition));
        _logger.ReceivedCalls().SelectMany(call => call.GetArguments()).OfType<Exception>().Should().BeEmpty();
        result.Value.Operations.Should().HaveCount(reads.Length);
        result.Value.Operations.Should().OnlyContain(operation => operation.Status == AgentOperationStatus.Succeeded && operation.ReadBlock != null);
        if (reads.Length > 0)
            result.Value.Operations!.Last().ReadBlock!.Block.Should().BeOfType<HabitListCard>().Which.Items.Should().BeEmpty();
        held.Should().ContainSingle();
        var preview = result.Value.PendingOperations.Should().ContainSingle().Subject;
        var reused = preview.Items.Should().ContainSingle(item => item.EntityId == existing.Id).Subject;
        reused.EntityName.Should().Be(existing.Title);
        reused.Fields.Single(field => field.Field == "title").ProposedValue!.Value.GetString().Should().Be(existing.Title);
        preview.Items!.Select(item => item.HabitType).Should().Equal("Task", "Habit", "Habit");
        result.Value.Parts.Should().BeEmpty();
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.FollowUps.Should().BeNullOrEmpty();
        result.Value.Actions.Should().BeEmpty();
        await _mediator.DidNotReceive().Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<UpdateHabitFieldsCommand>(), Arg.Any<CancellationToken>());
        var wire = JsonSerializer.Serialize(result.Value, ChatStreamEvent.SerializerOptions);
        var replay = JsonSerializer.Deserialize<ChatResponse>(wire, ChatStreamEvent.SerializerOptions)!;
        replay.PendingOperations!.Single().Items!.Single(item => item.EntityId == existing.Id).EntityName.Should().Be(existing.Title);
        ChatStreamEvent.Final(replay).ToJson().Should().Be(ChatStreamEvent.Final(result.Value).ToJson());
    }

    private static JsonDocument BrainDumpCapture(string name, Guid existingId) => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "BrainDumps", "g8-brain-dump-" + name + ".json"))
            .Replace("[redacted]", existingId.ToString()));

    [Theory]
    [InlineData("empty", false)]
    [InlineData("empty", true)]
    [InlineData("no_output", false)]
    [InlineData("no_output", true)]
    [InlineData("unavailable", false)]
    [InlineData("unavailable", true)]
    public async Task BrainDump_CapturedMixedRoundWaitsForReadsBeforeHoldingAnyPreview(string continuation, bool composition)
    {
        SetupUserAndPayGate();
        if (composition) EnableComposition();
        using var fixture = BrainDumpCapture("reuse-pt-BR-r1", Guid.NewGuid());
        var calls = BrainDumpCalls(fixture);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(query, bulk);
        var held = HoldBrainDumpWithReads(bulk, query);
        SetupAiResponse(new AiResponse { ToolCalls = calls, ConversationContext = new() });
        IReadOnlyList<AiToolCallResult>? forwarded = null;
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                held.Should().BeEmpty("the model must see the lookup before preparing the complete preview");
                forwarded = call.Arg<IReadOnlyList<AiToolCallResult>>();
                return continuation switch
                {
                    "no_output" => Result.Failure<AiResponse>(ErrorMessages.AiNoOutput),
                    "unavailable" => Result.Failure<AiResponse>(ErrorMessages.AiUnavailable),
                    _ => Result.Success(new AiResponse())
                };
            });

        var result = await handler.Handle(new(UserId, fixture.RootElement.GetProperty("message").GetString()!,
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true,
                SupportsHabitListCard: true, PartKinds: ["habitList"])), default);

        result.IsFailure.Should().BeTrue("supporting reads cannot fulfill the deferred creation");
        var expected = ErrorMessages.AiUnavailable;
        result.ErrorCode.Should().Be(expected.Code);
        result.Error.Should().Be(expected.Message);
        held.Should().BeEmpty();
        await _mediator.DidNotReceive().Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>());
        forwarded.Should().ContainSingle(item => item.Name == bulk.Name && item.Success);
        var deferred = JsonSerializer.SerializeToElement(forwarded!.Single(item => item.Name == bulk.Name).Payload,
            JsonSerializerOptions.Web);
        deferred.GetProperty("status").GetString().Should().Be("awaiting_reads");
        deferred.GetProperty("instruction").GetString().Should().Contain("Nothing was previewed or saved");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrainDump_CapturedMixedRoundResubmissionPreparesOneCombinedPreview(bool composition)
    {
        SetupUserAndPayGate();
        if (composition) EnableComposition();
        var today = new DateOnly(2026, 10, 28);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(today);
        var existing = Habit.Create(new(HabitSchedule.Recurring, UserId, "Beber água", FrequencyUnit.Day, 1, today)).Value;
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { existing }.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToArray());
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { existing }.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToArray());
        using var mixed = BrainDumpCapture("reuse-pt-BR-r1", existing.Id);
        using var complete = BrainDumpCapture("reuse-pt-BR-r3", existing.Id);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(query, bulk);
        var held = HoldBrainDumpWithReads(bulk, query);
        SetupAiResponse(new AiResponse { ToolCalls = BrainDumpCalls(mixed), ConversationContext = new() });
        var write = BrainDumpCalls(complete).Single(call => call.Name == bulk.Name) with { Id = "resubmitted" };
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                held.Should().BeEmpty("the incomplete mixed-round arguments must never be prepared");
                call.Arg<IReadOnlyList<AiToolCallResult>>().Should().Contain(item => item.Name == bulk.Name && item.Success);
                return Result.Success(new AiResponse { ToolCalls = [write], ConversationContext = new() });
            });

        var result = await handler.Handle(new(UserId, mixed.RootElement.GetProperty("message").GetString()!,
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true,
                SupportsHabitListCard: true, PartKinds: ["habitList"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        held.Should().ContainSingle().Which.Arguments.GetProperty("habits").GetArrayLength().Should().Be(3);
        var preview = result.Value.PendingOperations.Should().ContainSingle().Subject;
        preview.Items!.Select(item => item.HabitType).Should().Equal("Task", "Habit", "Habit");
        preview.Items.Should().ContainSingle(item => item.EntityId == existing.Id && item.EntityName == existing.Title);
        result.Value.Parts.Should().BeEmpty();
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.Actions.Should().BeEmpty();
        await _mediator.DidNotReceive().Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<UpdateHabitFieldsCommand>(), Arg.Any<CancellationToken>());
    }

    private static AiToolCall[] BrainDumpCalls(JsonDocument fixture) => fixture.RootElement.GetProperty("toolCalls")
        .EnumerateArray().Select((call, index) => new AiToolCall(call.GetProperty("name").GetString()!,
            "capture-" + index, call.GetProperty("arguments").Clone())).ToArray();

    private List<AgentExecuteOperationRequest> HoldBrainDumpWithReads(BulkCreateHabitsTool bulk, params IAiTool[] reads)
    {
        var blocks = UseProductionReadExecutor(reads);
        var executors = reads.ToDictionary(tool => tool.Name,
            tool => Orbit.Application.Tests.Chat.ReadBlockScopeTests.CreateExecutor(tool, blocks));
        var previewer = new PendingOperationChangePreviewer(_habitRepo, _goalRepo, _tagRepo,
            _userDateService, new AiToolRegistry([bulk]));
        var held = new List<AgentExecuteOperationRequest>();
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var request = call.Arg<AgentExecuteOperationRequest>();
            var ct = call.Arg<CancellationToken>();
            if (request.OperationId != bulk.Name)
                return await executors[request.OperationId].ExecuteAsync(request, ct);
            held.Add(request);
            var preview = (await previewer.PreviewAsync(UserId, request.OperationId, request.Arguments, ct)).Value!;
            var pending = new PendingAgentOperation(Guid.NewGuid(), bulk.Name, bulk.Name, bulk.Name,
                AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation, DateTime.UtcNow.AddMinutes(10),
                Changes: preview.Changes, ChangeTargetCount: preview.ChangeTargetCount, Items: preview.Items,
                PreviewFingerprint: preview.PreviewFingerprint);
            return new AgentExecuteOperationResponse(new(bulk.Name, bulk.Name, AgentRiskClass.Low,
                AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.PendingConfirmation), PendingOperation: pending);
        });
        return held;
    }
}
