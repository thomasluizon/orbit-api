using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

/// <summary>
/// The typed change list of the <c>ai_change_plan</c> path (thomasluizon/orbit-tickets#2160), on the real request path
/// with recorded model replies and the real pending operation store. Each test runs both declaration shapes.
/// </summary>
public partial class ProcessUserChatCommandHandlerTests
{
    private ChangeDeclarationShape _changeShape = ChangeDeclarationShape.Kinds;

    /// <summary>The in-memory databases of the real store, alive until the test ends.</summary>
    private readonly List<OrbitDbContext> _changeListDatabases = [];

    /// <summary>
    /// A model response that holds <paramref name="changes"/> in <paramref name="shape"/>: one call for each change, or
    /// one <c>propose_changes</c> call with the list. The first change call holds the plan title.
    /// </summary>
    private static AiResponse ChangeList(ChangeDeclarationShape shape, string? title, bool complete,
        params (string Id, string Kind, object Arguments)[] changes) => new()
        {
            ConversationContext = new(),
            ToolCalls = shape == ChangeDeclarationShape.Wrapper
                ? [new AiToolCall(ChangeDeclarations.WrapperName, "list", JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                {
                    [ChangeDeclarations.ChangesArgument] = changes.Select(change => new { id = change.Id, kind = change.Kind, arguments = change.Arguments }),
                    [ChangeDeclarations.PlanTitleArgument] = title,
                    [TerminalWriteDirective.CompleteArgument] = complete
                }))]
                : [.. changes.Select((change, index) => new AiToolCall(change.Kind, change.Id,
                    WithControls(change.Arguments, index == 0 ? title : null, complete)))]
        };

    private static JsonElement WithControls(object arguments, string? title, bool complete)
    {
        var node = JsonSerializer.SerializeToNode(arguments)!.AsObject();
        node[ChangeDeclarations.PlanTitleArgument] = title;
        node[TerminalWriteDirective.CompleteArgument] = complete;
        return JsonSerializer.SerializeToElement(node);
    }

    private static (string, string, object) DeleteChange(string id, IEnumerable<Habit> habits) =>
        (id, "bulk_delete_habits", new { habit_ids = habits.Select(habit => habit.Id.ToString()) });

    private static (string, string, object) RenameChange(string id, IEnumerable<object> habitIds, string title = RenameTitle) =>
        (id, "bulk_update_habits", new { filter = new { habit_ids = habitIds.Select(item => item.ToString()) }, updates = new { title } });

    private static (string, string, object) ExternalChange(string id, string note) => (id, ExternalTool, new { note });

    private const string ExternalTool = "manage_subscription";

    /// <summary>A write tool with no atomic support, as each external write is.</summary>
    private sealed class ExternalChangeTool : IAiTool
    {
        public string Name => ExternalTool;
        public string Description => "An external change.";
        public object GetParameterSchema() => new
        {
            type = "object",
            properties = new { note = new { type = "string" } },
            required = new[] { "note" }
        };
        public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) => Task.FromResult(new ToolResult(true));
    }

    private sealed record ChangeListRun(Result<ChatResponse> Result, Habit[] Children, Habit Parent,
        List<IReadOnlyList<AiToolCallResult>> ToolResults, List<ChatStreamEvent> Events, PendingAgentOperationStore Store,
        List<AiToolRequest> Requests);

    /// <summary>
    /// One turn over a parent "Water" with six "Water - 710ml" sub-habits, the real pending operation store, the bulk
    /// delete and update tools, the external tool and <paramref name="tools"/>. <paramref name="first"/> is the first model
    /// response and <paramref name="continuations"/> the recorded replies after it. The executor turns down a write whose
    /// habit targets hold an id that is not one of the person's habits.
    /// </summary>
    private async Task<ChangeListRun> RunChangeListAsync(ChangeDeclarationShape shape, string language,
        Func<Habit[], AiResponse> first, Func<Habit[], AiResponse[]> continuations, Action<PendingAgentOperationStore, Habit[]>? arrange = null,
        string[]? partKinds = null, bool trace = false, params IAiTool[] tools)
    {
        var database = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase("ChangeList_" + Guid.NewGuid()).Options);
        _changeListDatabases.Add(database);
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        if (trace)
            EnableChangePlan(FeatureFlagKeys.AiBlockFirst, FeatureFlagKeys.AiEvalTrace);
        else
            EnableChangePlan(FeatureFlagKeys.AiBlockFirst);
        _changeShape = shape;
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        var store = AnswerFromTheRealStore(database);
        var (parent, children) = SetupSameTitleChildren("Water - 710ml", 6);
        var delete = new BulkDeleteHabitsTool(_mediator, _habitRepo);
        var update = new BulkUpdateHabitsTool(_mediator);
        var external = new ExternalChangeTool();
        var handler = CreateHandler([delete, update, external, .. tools]);
        HoldWritesIn(store, delete, update);
        HoldExternalWritesIn(store);
        DenyUnownedHabitTargets([parent, .. children]);
        arrange?.Invoke(store, children);
        var requests = new List<AiToolRequest>();
        _aiIntentService.SendWithToolsAsync(Arg.Do<AiToolRequest>(requests.Add), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(first(children)));
        var toolResults = new List<IReadOnlyList<AiToolCallResult>>();
        var replies = continuations(children).Select(reply => Result.Success(reply)).ToArray();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Do<IReadOnlyList<AiToolCallResult>>(results => toolResults.Add([.. results])),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(replies[0], replies[1..]);
        var events = new List<ChatStreamEvent>();
        var result = await handler.Handle(new ProcessUserChatCommand(UserId, language == "en" ? OwnerSplitMessageEn : OwnerSplitMessagePt,
            EvalTraceRequested: trace,
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, PartKinds: partKinds ?? ["choice", "question"], Locale: language),
            StreamSink: item =>
            {
                events.Add(item);
                return Task.CompletedTask;
            }), default);
        return new ChangeListRun(result, children, parent, toolResults, events, store, requests);
    }

    /// <summary>Each external write that the executor holds prepares its row in <paramref name="store"/> with one item.</summary>
    private void HoldExternalWritesIn(PendingAgentOperationStore store) =>
        _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId == ExternalTool),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<AgentExecuteOperationRequest>();
                var pending = StoreHeldWrite(store, ExternalTool, request.Arguments.GetRawText(), "external-" + request.Arguments.GetRawText());
                return new AgentExecuteOperationResponse(new AgentOperationResult(ExternalTool, ExternalTool, AgentRiskClass.Low,
                        AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.PendingConfirmation),
                    PendingOperation: pending with
                    {
                        ChangeTargetCount = 1,
                        Items = [new PendingOperationItem("0", null, "Subscription", [], "state")]
                    });
            });

    private static IReadOnlyList<ChangeError> ErrorsOf(AiToolCallResult result) =>
        JsonSerializer.SerializeToElement(result.Payload).GetProperty("errors").Deserialize<ChangeError[]>() ?? [];

    private static AiPlan? FinalPlan(IEnumerable<ChatStreamEvent> events)
    {
        AiPlan? plan = null;
        foreach (var item in events)
        {
            if (item.Type == "plan") plan = item.Plan;
            else if (item.Type == "plan_step" && plan is not null && item.PlanStep is { } step)
                plan = plan with { Steps = [.. plan.Steps.Select(current => current.Id == step.Id ? step : current)] };
        }
        return plan;
    }

    [Theory]
    [InlineData(ChangeDeclarationShape.Kinds, "en")]
    [InlineData(ChangeDeclarationShape.Kinds, "pt-BR")]
    [InlineData(ChangeDeclarationShape.Wrapper, "en")]
    [InlineData(ChangeDeclarationShape.Wrapper, "pt-BR")]
    public async Task ChangeList_TwoDeletesAndFourRenamesFormOneChangeSetWithSixItemsAndAPlan(ChangeDeclarationShape shape, string language)
    {
        var title = language == "en" ? "Tidy the Water habits" : "Organizar os hábitos Water";
        var run = await RunChangeListAsync(shape, language,
            children => ChangeList(shape, title, true, DeleteChange("delete", children[4..]),
                RenameChange("rename", children[..4].Select(child => (object)child.Id))),
            _ => [Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.ToolResults.Should().BeEmpty("a complete list that prepares its previews ends the turn with no second model call");
        var preview = run.Result.Value.PendingOperations.Should().ContainSingle().Subject;
        preview.CapabilityId.Should().Be(AccountChangeSet.CapabilityId);
        preview.Items.Should().HaveCount(6);
        preview.Items!.Where(item => item.Fields.Any(field => field.Field == "delete")).Select(item => item.EntityId)
            .Should().BeEquivalentTo(run.Children[4..].Select(child => child.Id));
        preview.Items!.Where(item => item.Fields.Any(field => field.Field == "title")).Select(item => item.EntityId)
            .Should().BeEquivalentTo(run.Children[..4].Select(child => child.Id));
        var plan = FinalPlan(run.Events);
        plan.Should().NotBeNull("the server builds the plan from the change list");
        plan!.Title.Should().Be(title);
        plan.Steps.Should().HaveCount(2).And.OnlyContain(step => step.Phase == "approval" && step.PendingOperationId == preview.Id
            && step.Text == "Water - 710ml");
        plan.Steps.Select(step => step.Tool).Should().Equal("bulk_delete_habits", "bulk_update_habits");
        var names = run.Requests.Should().ContainSingle().Subject.ToolDeclarations
            .Select(declaration => JsonSerializer.SerializeToElement(declaration).GetProperty("name").GetString()).ToArray();
        names.Should().NotContain(PlanToolDeclaration.Name).And.NotContain("delete_habit");
        if (shape == ChangeDeclarationShape.Wrapper)
            names.Should().Contain(ChangeDeclarations.WrapperName).And.NotContain("bulk_delete_habits");
        else
            names.Should().Contain("bulk_delete_habits").And.NotContain(ChangeDeclarations.WrapperName);
    }

    [Theory]
    [InlineData(ChangeDeclarationShape.Kinds, "en")]
    [InlineData(ChangeDeclarationShape.Wrapper, "pt-BR")]
    public async Task ChangeList_OneFailedChangeBesideThreeHoldsNothingAndNamesOnlyThatChange(ChangeDeclarationShape shape, string language)
    {
        var unknown = Guid.NewGuid();
        var run = await RunChangeListAsync(shape, language,
            children => ChangeList(shape, null, true, DeleteChange("delete", children[4..]),
                RenameChange("rename-a", children[..2].Select(child => (object)child.Id)),
                RenameChange("rename-b", [children[2].Id, children[3].Id], "Water - 1L"),
                RenameChange("rename-c", [unknown], "Water - 2L")),
            _ => [Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty();
        (await run.Store.GetAwaitingAsync(UserId)).Should().BeEmpty("the server holds nothing of a list with an error");
        var errors = run.ToolResults.Should().ContainSingle().Subject.Where(result => !result.Success).Select(ErrorsOf)
            .Should().NotBeEmpty().And.Subject.SelectMany(list => list).Distinct().ToArray();
        errors.Should().ContainSingle().Which.Should().Match<ChangeError>(error => error.Change == "rename-c"
            && error.Code == "target_not_owned:bulk_update_habits:habit");
        run.Result.Value.AiMessage.Should().Be(NotPreparedFor(language));
    }

    /// <summary>
    /// A chat round gets the status <c>Succeeded</c> for a write before approval only when the write changes nothing: the
    /// executor closes the row of a bulk change with no change and returns the no-changes result. A list with that change
    /// first and a failed change second saves nothing and holds nothing, and the error list names the failed change only.
    /// </summary>
    [Theory]
    [InlineData(ChangeDeclarationShape.Kinds, "en")]
    [InlineData(ChangeDeclarationShape.Kinds, "pt-BR")]
    [InlineData(ChangeDeclarationShape.Wrapper, "en")]
    [InlineData(ChangeDeclarationShape.Wrapper, "pt-BR")]
    public async Task ChangeList_AWriteWithNoChangeBesideAFailedChangeSavesNothingAndHoldsNothing(ChangeDeclarationShape shape, string language)
    {
        const string sameTitle = "Water - 710ml";
        var writes = new List<AgentExecuteOperationRequest>();
        var run = await RunChangeListAsync(shape, language,
            children => ChangeList(shape, null, true, RenameChange("no-change", [children[0].Id], sameTitle),
                RenameChange("unknown", [Guid.NewGuid()])),
            _ => [Text(language)],
            arrange: (_, _) => _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request =>
                    request.OperationId == "bulk_update_habits" && request.Arguments.GetRawText().Contains(sameTitle)),
                    Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var request = call.Arg<AgentExecuteOperationRequest>();
                    writes.Add(request);
                    return new AgentExecuteOperationResponse(new AgentOperationResult(request.OperationId, request.OperationId,
                        AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.Succeeded,
                        Payload: BulkNoChangesOutcome.For(request.OperationId, request.Arguments), Access: "write"));
                }));

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        writes.Should().ContainSingle("the change with no change ran first").Which.ConfirmationToken.Should().BeNull();
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty();
        run.Result.Value.Actions.Should().NotContain(action => action.Status == ActionStatus.Success);
        (await run.Store.GetAwaitingAsync(UserId)).Should().BeEmpty("the server holds nothing of a list with an error");
        run.ToolResults.Should().ContainSingle().Subject.Where(result => !result.Success).SelectMany(ErrorsOf).Distinct()
            .Should().ContainSingle().Which.Should().Match<ChangeError>(error => error.Change == "unknown"
                && error.Code == "target_not_owned:bulk_update_habits:habit");
        run.Result.Value.AiMessage.Should().Be(NotPreparedFor(language));
    }

    [Theory]
    [InlineData(ChangeDeclarationShape.Kinds, "en")]
    [InlineData(ChangeDeclarationShape.Kinds, "pt-BR")]
    [InlineData(ChangeDeclarationShape.Wrapper, "en")]
    [InlineData(ChangeDeclarationShape.Wrapper, "pt-BR")]
    public async Task ChangeList_RepairRoundPreparesTheCorrectedList(ChangeDeclarationShape shape, string language)
    {
        var run = await RunChangeListAsync(shape, language,
            children => ChangeList(shape, null, true, DeleteChange("delete", children[4..]),
                RenameChange("rename", [children[0].Id, CutShort(children[1].Id), children[2].Id, children[3].Id])),
            children => [ChangeList(shape, null, true, DeleteChange("delete", children[4..]),
                RenameChange("rename", children[..4].Select(child => (object)child.Id))), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.ToolResults.Should().ContainSingle("the repaired list is complete and ends the turn");
        var preview = run.Result.Value.PendingOperations.Should().ContainSingle().Subject;
        preview.Items.Should().HaveCount(6);
        run.Result.Value.AiMessage.Should().NotBe(NotPreparedFor(language));
    }

    [Theory]
    [InlineData(ChangeDeclarationShape.Kinds, "en")]
    [InlineData(ChangeDeclarationShape.Kinds, "pt-BR")]
    [InlineData(ChangeDeclarationShape.Wrapper, "en")]
    [InlineData(ChangeDeclarationShape.Wrapper, "pt-BR")]
    public async Task ChangeList_ASecondFailedListEndsTheTurnWithNoPreviewAndSaysSo(ChangeDeclarationShape shape, string language)
    {
        var failing = (Habit[] children) => ChangeList(shape, null, true, DeleteChange("delete", children[4..]),
            RenameChange("rename", [children[0].Id, CutShort(children[1].Id)]));
        var run = await RunChangeListAsync(shape, language, failing, children => [failing(children), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.ToolResults.Should().ContainSingle("the model gets one repair round and no third call");
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty();
        (await run.Store.GetAwaitingAsync(UserId)).Should().BeEmpty();
        run.Result.Value.AiMessage.Should().Be(NotPreparedFor(language));
    }

    private static AiResponse WrapperCall(string arguments) => new()
    {
        ConversationContext = new(),
        ToolCalls = [new AiToolCall(ChangeDeclarations.WrapperName, "list", JsonDocument.Parse(arguments).RootElement.Clone())]
    };

    /// <summary>
    /// A <c>propose_changes</c> call with no readable change is a typed error of the list, not a prepared list. The model
    /// gets its one repair round, and the eval trace records the failed call.
    /// </summary>
    [Theory]
    [InlineData("""{"changes":[],"plan_title":null,"orbit_write_complete":true}""")]
    [InlineData("""{"plan_title":null,"orbit_write_complete":true}""")]
    [InlineData("""{"changes":"none","plan_title":null,"orbit_write_complete":true}""")]
    public async Task ChangeList_AWrapperCallWithNoChangeReturnsATypedErrorAndKeepsTheRepairRound(string arguments)
    {
        var run = await RunChangeListAsync(ChangeDeclarationShape.Wrapper, "en", _ => WrapperCall(arguments),
            children => [ChangeList(ChangeDeclarationShape.Wrapper, null, true, DeleteChange("delete", children[5..])), Text("en")],
            trace: true);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        var refused = run.ToolResults.Should().ContainSingle("the repaired list is complete and ends the turn").Subject
            .Should().ContainSingle().Subject;
        refused.Name.Should().Be(ChangeDeclarations.WrapperName);
        refused.Success.Should().BeFalse();
        refused.Error.Should().Be(ChangeListCompiler.NotPrepared);
        ErrorsOf(refused).Should().Equal(new ChangeError("list", ChangeDeclarations.ChangesArgument,
            ChangeListCompiler.InvalidArguments, "The change list holds no change. Send one item in changes for each change."));
        run.Result.Value.PendingOperations.Should().ContainSingle().Which.Items!.Select(item => item.EntityId)
            .Should().Equal(run.Children[5].Id);
        var traced = run.Result.Value.EvalTrace!.ToolCalls;
        traced.Select(call => (call.Id, call.Name, call.Status)).Should().Equal(
            ("list", ChangeDeclarations.WrapperName, "Failed"),
            ("delete", "bulk_delete_habits", nameof(AgentOperationStatus.PendingConfirmation)));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task ChangeList_ASecondWrapperCallWithNoChangeEndsTheTurnWithNoPreviewAndSaysSo(string language)
    {
        const string empty = """{"changes":[],"plan_title":null,"orbit_write_complete":true}""";
        var run = await RunChangeListAsync(ChangeDeclarationShape.Wrapper, language, _ => WrapperCall(empty),
            _ => [WrapperCall(empty), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.ToolResults.Should().ContainSingle("the model gets one repair round and no third call");
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty();
        (await run.Store.GetAwaitingAsync(UserId)).Should().BeEmpty();
        run.Result.Value.AiMessage.Should().Be(NotPreparedFor(language));
    }

    [Theory]
    [InlineData(ChangeDeclarationShape.Kinds)]
    [InlineData(ChangeDeclarationShape.Wrapper)]
    public async Task ChangeList_AnExternalChangeGetsItsOwnPreviewAndNeverJoinsTheChangeSet(ChangeDeclarationShape shape)
    {
        var run = await RunChangeListAsync(shape, "en",
            children => ChangeList(shape, "Water and plan", true, DeleteChange("delete", children[4..]),
                RenameChange("rename", children[..4].Select(child => (object)child.Id)), ExternalChange("plan", "yearly")),
            _ => [Text("en")]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        var previews = run.Result.Value.PendingOperations.Should().HaveCount(2).And.Subject.ToArray();
        var set = previews.Should().ContainSingle(preview => preview.CapabilityId == AccountChangeSet.CapabilityId).Subject;
        var members = AccountChangeSet.Read(run.Store.GetExecution(UserId, set.Id)!.Arguments)!;
        members.Select(member => member.OperationId).Should().BeEquivalentTo(["bulk_delete_habits", "bulk_update_habits"]);
        previews.Should().ContainSingle(preview => preview.CapabilityId == ExternalTool);
    }

    [Theory]
    [InlineData(ChangeDeclarationShape.Kinds)]
    [InlineData(ChangeDeclarationShape.Wrapper)]
    public async Task ChangeList_AFourthPreviewReturnsTooManyPreviews(ChangeDeclarationShape shape)
    {
        var run = await RunChangeListAsync(shape, "en",
            _ => ChangeList(shape, null, true, ExternalChange("a", "1"), ExternalChange("b", "2"), ExternalChange("c", "3"),
                ExternalChange("d", "4")),
            _ => [Text("en")]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty();
        (await run.Store.GetAwaitingAsync(UserId)).Should().BeEmpty();
        run.ToolResults.Should().ContainSingle().Subject.SelectMany(ErrorsOf).Distinct().Should().ContainSingle()
            .Which.Should().Match<ChangeError>(error => error.Code == ChangeListCompiler.TooManyPreviews && error.Change == "d");
    }

    [Theory]
    [InlineData(ChangeDeclarationShape.Kinds)]
    [InlineData(ChangeDeclarationShape.Wrapper)]
    public async Task ChangeList_ThreeOlderOpenPreviewsDoNotBlockANewList(ChangeDeclarationShape shape)
    {
        var run = await RunChangeListAsync(shape, "en",
            children => ChangeList(shape, null, true, DeleteChange("delete", children[4..]), ExternalChange("plan", "yearly"),
                ExternalChange("key", "rotate")),
            _ => [Text("en")],
            arrange: (store, _) =>
            {
                foreach (var index in Enumerable.Range(0, 3))
                    StoreHeldWrite(store, ExternalTool, JsonSerializer.Serialize(new { note = "older " + index }), "older-" + index);
            });

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Result.Value.PendingOperations.Should().HaveCount(3);
        (await run.Store.GetAwaitingAsync(UserId)).Should().HaveCount(6);
    }

    [Theory]
    [InlineData(ChangeDeclarationShape.Kinds, "choice")]
    [InlineData(ChangeDeclarationShape.Wrapper, "choice")]
    [InlineData(ChangeDeclarationShape.Kinds, "pickList")]
    [InlineData(ChangeDeclarationShape.Wrapper, "pickList")]
    public async Task ChangeList_AChangeOnARecordOfTheAskWaitsAndAnIndependentChangeCompiles(ChangeDeclarationShape shape, string form)
    {
        var issued = new List<IssuedAiBlock>();
        _blockStore.AddAsync(Arg.Do<IssuedAiBlock>(issued.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        AiToolCall Ask(Habit[] children) => form == "choice"
            ? new("ask", "ask", JsonSerializer.SerializeToElement(new
            {
                input = new
                {
                    form = "choice",
                    missing_input = "record_selection",
                    question = "Which Water - 710ml?",
                    about = children[..2].Select(child => child.Id.ToString()),
                    allowFreeText = false
                }
            }))
            : ModelPickList(children[..2], "log_habits");
        var run = await RunChangeListAsync(shape, "en", children =>
        {
            var list = ChangeList(shape, null, false, RenameChange("dependent", [children[0].Id]), DeleteChange("independent", children[5..]));
            return list with { ToolCalls = [.. list.ToolCalls!, Ask(children)] };
        }, _ => [Text("en")], partKinds: ["choice", "question", "pickList"], tools: [RealAskTool()]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.ToolResults.Should().BeEmpty("the question ends the reply");
        issued.Should().ContainSingle();
        var preview = run.Result.Value.PendingOperations.Should().ContainSingle().Subject;
        preview.Items!.Select(item => item.EntityId).Should().Equal(run.Children[5].Id);
        _heldPreviews.Values.Should().NotContain(held => held.Request.OperationId == "bulk_update_habits",
            "the dependent change waits for the answer");
    }

    [Theory]
    [InlineData(ChangeDeclarationShape.Kinds)]
    [InlineData(ChangeDeclarationShape.Wrapper)]
    public async Task ChangeList_AWaitingChangeReturnsWaitsForAnswerToTheModel(ChangeDeclarationShape shape)
    {
        var run = await RunChangeListAsync(shape, "en", children =>
        {
            var list = ChangeList(shape, null, false, RenameChange("dependent", [children[0].Id]), DeleteChange("independent", children[5..]));
            return list with
            {
                ToolCalls = [.. list.ToolCalls!, new AiToolCall("ask", "ask", JsonSerializer.SerializeToElement(new
                {
                    input = new { form = "choice", missing_input = "record_selection", question = "Which?", about = new[] { children[0].Id.ToString() } }
                }))]
            };
        }, _ => [Text("en")], tools: [RealAskTool()]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        var results = run.ToolResults.Should().ContainSingle("an ask about one record fails, so the turn continues").Subject;
        var waiting = shape == ChangeDeclarationShape.Wrapper
            ? JsonSerializer.SerializeToElement(results.Single(result => result.Name == ChangeDeclarations.WrapperName).Payload)
                .GetProperty("waiting").Deserialize<ChangeError[]>()!
            : ErrorsOf(results.Single(result => result.Id == "dependent")).ToArray();
        waiting.Should().ContainSingle().Which.Should().Match<ChangeError>(error => error.Change == "dependent"
            && error.Code == ChangeListCompiler.WaitsForAnswer && error.Candidates!.Single() == run.Children[0].Id.ToString());
    }

    /// <summary>
    /// A recorded offer turn of <c>create_habit</c> sent as a create change of the change list: the executor gets the same
    /// arguments with the same offer metadata, and the preview holds the same fields with the same provenance and removal.
    /// </summary>
    [Theory]
    [InlineData(ChangeDeclarationShape.Kinds, "repeat-en-r1")]
    [InlineData(ChangeDeclarationShape.Wrapper, "repeat-en-r1")]
    [InlineData(ChangeDeclarationShape.Kinds, "repeat-pt-BR-r3")]
    [InlineData(ChangeDeclarationShape.Wrapper, "repeat-pt-BR-r3")]
    [InlineData(ChangeDeclarationShape.Kinds, "dated-reminder-pt-BR-r2")]
    public async Task ChangeList_AnOfferOnACreateChangeGivesTheSamePreviewAndOfferMetadata(ChangeDeclarationShape shape, string capture)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var language = capture.Contains("pt-BR") ? "pt-BR" : "en";
        var create = new CreateHabitTool(_userDateService, _mediator);
        var bulk = new BulkCreateHabitsTool(_mediator);
        var recorded = R9fCall(capture, create.Name);
        var oldPath = CreateHandler(create);
        var outcomes = R9fWriteExecutor(create);
        SetupAiResponse(new AiResponse { ToolCalls = [recorded], ConversationContext = TestConversationContext });
        var before = await oldPath.Handle(new(UserId, "Prepare the requested preview.", ClientContext: OfferClient(language)), default);

        _changeShape = shape;
        EnableChangePlan(FeatureFlagKeys.AiBlockFirst);
        var habit = JsonNode.Parse(recorded.Args.GetRawText())!.AsObject();
        habit.Remove(TerminalWriteDirective.CompleteArgument);
        var changePath = CreateHandler(create, bulk);
        var changeOutcomes = R9fWriteExecutor(create);
        SetupAiResponse(ChangeList(shape, null, true, ("habit", "bulk_create_habits", new { habits = new[] { habit } })));
        var after = await changePath.Handle(new(UserId, "Prepare the requested preview.", ClientContext: OfferClient(language)), default);

        before.IsSuccess.Should().BeTrue(before.Error);
        after.IsSuccess.Should().BeTrue(after.Error);
        var held = outcomes.Should().ContainSingle().Subject;
        var compiled = changeOutcomes.Should().ContainSingle().Subject;
        compiled.Operation.OperationId.Should().Be(held.Operation.OperationId);
        static object Fields(PendingAgentOperation preview) => preview.Items!.Single().Fields.Select(field =>
            new { field.Field, field.NewValue, field.Provenance, Removal = JsonSerializer.Serialize(field.Removal) }).ToArray();
        var expected = before.Value.PendingOperations.Should().ContainSingle().Subject;
        var actual = after.Value.PendingOperations.Should().ContainSingle().Subject;
        Fields(actual).Should().BeEquivalentTo(Fields(expected), options => options.WithStrictOrdering());
        actual.Items!.Single().Fields.Should().Contain(field => field.Provenance == "suggested" && field.Removal != null);
    }

    [Theory]
    [InlineData(ChangeDeclarationShape.Kinds)]
    [InlineData(ChangeDeclarationShape.Wrapper)]
    public async Task ChangeList_TheEvalTraceRecordsEachChangeWithItsKindAndArguments(ChangeDeclarationShape shape)
    {
        var run = await RunChangeListAsync(shape, "en",
            children => ChangeList(shape, null, true, DeleteChange("delete", children[4..]),
                RenameChange("rename", children[..4].Select(child => (object)child.Id))),
            _ => [Text("en")], trace: true);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        var calls = run.Result.Value.EvalTrace!.ToolCalls;
        calls.Select(call => (call.Id, call.Name)).Should().Equal(("delete", "bulk_delete_habits"), ("rename", "bulk_update_habits"));
        calls[0].Arguments.GetProperty("habit_ids").EnumerateArray().Select(id => id.GetString())
            .Should().Equal(run.Children[4..].Select(child => child.Id.ToString()));
        calls[1].Arguments.GetProperty("updates").GetProperty("title").GetString().Should().Be(RenameTitle);
        calls.Should().OnlyContain(call => call.Status == nameof(AgentOperationStatus.PendingConfirmation));
    }

    /// <summary>
    /// A list with an error beside an <c>ask</c> issues no question, so the turn keeps its repair round. The repaired list
    /// prepares its preview and the question of the repair shows beside it.
    /// </summary>
    [Theory]
    [InlineData(ChangeDeclarationShape.Kinds)]
    [InlineData(ChangeDeclarationShape.Wrapper)]
    public async Task ChangeList_AFailedListBesideAnAskIssuesNoQuestionAndKeepsTheRepairRound(ChangeDeclarationShape shape)
    {
        var issued = new List<IssuedAiBlock>();
        _blockStore.AddAsync(Arg.Do<IssuedAiBlock>(issued.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        AiResponse WithAsk(AiResponse list, Habit[] children) => list with
        {
            ToolCalls = [.. list.ToolCalls!, new AiToolCall("ask", "ask", JsonSerializer.SerializeToElement(new
            {
                input = new { form = "choice", missing_input = "record_selection", question = "Which?", about = children[..2].Select(child => child.Id.ToString()) }
            }))]
        };
        var run = await RunChangeListAsync(shape, "en",
            children => WithAsk(ChangeList(shape, null, false, DeleteChange("delete", children[5..]), RenameChange("rename", [Guid.NewGuid()])), children),
            children => [WithAsk(ChangeList(shape, null, false, DeleteChange("delete", children[5..])), children), Text("en")],
            tools: [RealAskTool()]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        var first = run.ToolResults.Should().ContainSingle().Subject;
        first.Single(result => result.Id == "ask").Error.Should().Be(ChangeListCompiler.NotPrepared);
        issued.Should().ContainSingle("only the repair round issues the question");
        run.Result.Value.PendingOperations.Should().ContainSingle().Which.Items!.Select(item => item.EntityId)
            .Should().Equal(run.Children[5].Id);
    }
}
