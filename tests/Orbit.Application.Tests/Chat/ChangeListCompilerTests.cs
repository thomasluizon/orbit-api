using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

/// <summary>
/// The compiler of the typed change list (thomasluizon/orbit-tickets#2160) with no model: each change runs through its
/// write tool call, and each error path gives its typed error.
/// </summary>
public sealed class ChangeListCompilerTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private readonly IPendingAgentOperationStore _store = Substitute.For<IPendingAgentOperationStore>();
    private readonly Dictionary<Guid, PendingAgentOperationExecution> _stored = [];

    public ChangeListCompilerTests() =>
        _store.GetExecution(UserId, Arg.Any<Guid>()).Returns(call => _stored.GetValueOrDefault(call.ArgAt<Guid>(1)));

    private sealed class FakeTool(string name, bool readOnly = false, bool? atomic = null) : IAiTool, ITransactionalAiTool
    {
        public string Name => name;
        public string Description => name;
        public bool IsReadOnly => readOnly;
        public object GetParameterSchema() => new { type = "object", properties = new { } };
        public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) => Task.FromResult(new ToolResult(true));
        public bool SupportsAtomicExecution(JsonElement arguments) => atomic == true;
    }

    private sealed class ExternalTool(string name) : IAiTool
    {
        public string Name => name;
        public string Description => name;
        public object GetParameterSchema() => new { type = "object", properties = new { } };
        public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) => Task.FromResult(new ToolResult(true));
    }

    private sealed class McpOnlyTool : IAiTool, IMcpOnlyAiTool
    {
        public string Name => "manage_feature_flags";
        public string Description => Name;
        public object GetParameterSchema() => new { type = "object" };
        public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) => Task.FromResult(new ToolResult(true));
    }

    private static readonly AiToolRegistry Registry = new([
        new FakeTool("bulk_delete_habits", atomic: true), new FakeTool("bulk_update_habits", atomic: true),
        new FakeTool("delete_habit"), new FakeTool("log_habit"), new FakeTool("bulk_log_habits"), new FakeTool("create_habit"),
        new FakeTool("create_sub_habit"), new FakeTool("bulk_create_habits"), new FakeTool("query_habits", readOnly: true),
        new ExternalTool("manage_subscription"), new McpOnlyTool()]);

    private static ProposedChange Change(string id, string kind, object? arguments = null) =>
        new(id, kind, JsonSerializer.SerializeToElement(arguments ?? new { }));

    private PendingAgentOperation Prepare(string operation, JsonElement arguments)
    {
        var pending = new PendingAgentOperation(Guid.NewGuid(), operation, operation, operation, AgentRiskClass.Low,
            AgentConfirmationRequirement.FreshConfirmation, DateTime.UtcNow.AddMinutes(10),
            Items: [new PendingOperationItem("0", Guid.NewGuid(), "Water", [], "state")], PreviewFingerprint: "preview");
        _stored[pending.Id] = new PendingAgentOperationExecution(pending.Id, operation, operation, arguments,
            AgentExecutionSurface.Chat, AgentConfirmationRequirement.FreshConfirmation, "preview");
        return pending;
    }

    private Func<IReadOnlyList<AiToolCall>, CancellationToken, Task<IReadOnlyList<ChangeResult>>> Prepares(List<AiToolCall> dispatched) =>
        (calls, _) =>
        {
            dispatched.AddRange(calls);
            return Task.FromResult<IReadOnlyList<ChangeResult>>(
                [.. calls.Select(call => new ChangeResult(call.Id, Prepare(call.Name, call.Args), null))]);
        };

    [Fact]
    public void Kinds_AreTheChatWriteToolsWithTheSingleFormsMergedIntoTheirBulkForm()
    {
        ChangeKinds.All(Registry).Select(tool => tool.Name).Should().Equal(
            "bulk_create_habits", "bulk_delete_habits", "bulk_log_habits", "bulk_update_habits", "manage_subscription");
        foreach (var single in new[] { "delete_habit", "log_habit", "create_habit", "create_sub_habit" })
            ChangeKinds.KindOf(single).Should().StartWith("bulk_");
        ChangeKinds.KindOf("update_habit").Should().Be("update_habit");
    }

    [Theory]
    [InlineData("bulk_delete_habits", """{"habit_ids":["a"]}""")]
    [InlineData("bulk_update_habits", """{"filter":{"habit_ids":["a"]},"updates":{"title":"Water"}}""")]
    [InlineData("bulk_log_habits", """{"habit_ids":["a"],"date":"2026-04-03"}""")]
    [InlineData("bulk_create_habits", """{"habits":[{"title":"Walk"},{"title":"Run"}]}""")]
    [InlineData("manage_subscription", """{"action":"cancel"}""")]
    public async Task EachKind_RunsItsWriteToolWithItsArgumentsAndGivesItsPreview(string kind, string arguments)
    {
        var dispatched = new List<AiToolCall>();
        var change = new ProposedChange("one", kind, JsonDocument.Parse(arguments).RootElement);

        var outcome = await new ChangeListCompiler(Registry).CompileAsync(UserId, [change], new HashSet<Guid>(),
            Prepares(dispatched), _store, default);

        outcome.IsSuccess.Should().BeTrue();
        var call = dispatched.Should().ContainSingle().Subject;
        call.Name.Should().Be(kind);
        call.Id.Should().Be("one");
        call.Args.GetRawText().Should().Be(change.Arguments.GetRawText());
        outcome.Previews.Should().ContainSingle().Which.CapabilityId.Should().Be(kind);
    }

    [Fact]
    public void ACreateWithOneHabitRunsCreateHabitAndTakesTheParentOfTheList()
    {
        var parent = Guid.NewGuid().ToString();
        var call = ChangeKinds.Dispatch(new AiToolCall("bulk_create_habits", "new",
            JsonSerializer.SerializeToElement(new { parent_habit_id = parent, habits = new[] { new { title = "Walk" } } })));

        call.Name.Should().Be("create_habit");
        call.Id.Should().Be("new");
        call.Args.GetProperty("title").GetString().Should().Be("Walk");
        call.Args.GetProperty("parent_habit_id").GetString().Should().Be(parent);
        var many = new AiToolCall("bulk_create_habits", "many",
            JsonSerializer.SerializeToElement(new { habits = new[] { new { title = "Walk" }, new { title = "Run" } } }));
        ChangeKinds.Dispatch(many).Should().Be(many);
    }

    [Fact]
    public void Check_GivesUnknownKindDuplicateChangeAndInvalidArgumentsBeforeAnyWrite()
    {
        var check = new ChangeListCompiler(Registry).Check([
            Change("a", "delete_habit"), Change("b", "bulk_delete_habits"), Change("b", "bulk_update_habits"),
            new ProposedChange("c", "bulk_log_habits", JsonSerializer.SerializeToElement("text")), Change("d", "query_habits")],
            new HashSet<Guid>());

        check.Runnable.Select(call => call.Id).Should().Equal("b");
        check.Errors.Select(error => (error.Change, error.Path, error.Code)).Should().Equal(
            ("a", "kind", ChangeListCompiler.UnknownKind), ("b", "id", ChangeListCompiler.DuplicateChange),
            ("c", "arguments", "invalid_arguments"), ("d", "kind", ChangeListCompiler.UnknownKind));
        check.Errors[0].Allowed.Should().Equal(ChangeKinds.All(Registry).Select(tool => tool.Name));
    }

    [Fact]
    public void Check_AChangeThatNamesARecordOfTheAskWaitsAndIsNoError()
    {
        var asked = Guid.NewGuid();
        var check = new ChangeListCompiler(Registry).Check([
            Change("dependent", "bulk_update_habits", new { filter = new { habit_ids = new[] { asked } } }),
            Change("independent", "bulk_delete_habits", new { habit_ids = new[] { Guid.NewGuid() } })], new HashSet<Guid> { asked });

        check.Errors.Should().BeEmpty();
        check.Runnable.Select(call => call.Id).Should().Equal("independent");
        check.Waiting.Should().ContainSingle().Which.Should().Be(new ChangeError("dependent", "arguments",
            ChangeListCompiler.WaitsForAnswer, "This change names a record of the question in the same response.")
        { Candidates = check.Waiting[0].Candidates });
        check.Waiting[0].Candidates.Should().Equal(asked.ToString());
    }

    [Fact]
    public void RecordIds_ReadsTheArgumentsOfAPickListItemWrittenAsJsonText()
    {
        var id = Guid.NewGuid();
        var ask = JsonSerializer.SerializeToElement(new
        {
            form = "pickList",
            items = new[] { new { id = "1", arguments = JsonSerializer.Serialize(new { habit_id = id }) } }
        });

        ChangeListCompiler.RecordIds(ask).Should().Equal(id);
    }

    /// <summary>The change "c" fails with <c>NOT_FOUND</c>; each other change prepares its preview in <paramref name="prepared"/>.</summary>
    private Func<IReadOnlyList<AiToolCall>, CancellationToken, Task<IReadOnlyList<ChangeResult>>> FailsTheLogChange(
        List<PendingAgentOperation> prepared) => (calls, _) => Task.FromResult<IReadOnlyList<ChangeResult>>([.. calls.Select(call =>
        {
            if (call.Id == "c")
                return new ChangeResult("c", null, ChangeListCompiler.ErrorOf("c",
                    new AgentOperationResult("bulk_log_habits", "bulk_log_habits", AgentRiskClass.Low, AgentConfirmationRequirement.None,
                        AgentOperationStatus.Failed, PolicyReason: "No matching habits found.", ErrorCode: "NOT_FOUND"),
                    new AiToolCallResult("bulk_log_habits", "c", false, null, null, "No matching habits found."), null));
            var preview = Prepare(call.Name, call.Args);
            prepared.Add(preview);
            return new ChangeResult(call.Id, preview, null);
        })]);

    /// <summary>
    /// The chat round and the pickList answer use one compile flow. A caller with a ledger of the turn gets the previews
    /// of a failed list and decides each withdrawal, so the compiler withdraws nothing in the store by itself.
    /// </summary>
    [Fact]
    public async Task AFailedListGivesItsPreviewsToTheWithdrawalOfTheCallerAndWithdrawsNothingByItself()
    {
        var prepared = new List<PendingAgentOperation>();
        var withdrawn = new List<IReadOnlyList<PendingAgentOperation>>();
        var outcome = await new ChangeListCompiler(Registry).CompileAsync(UserId,
            [Change("a", "bulk_delete_habits"), Change("b", "bulk_update_habits"), Change("c", "bulk_log_habits")],
            new HashSet<Guid>(), FailsTheLogChange(prepared), _store, default, withdraw: withdrawn.Add);

        outcome.IsSuccess.Should().BeFalse();
        outcome.Previews.Should().BeEmpty();
        outcome.Results.Select(result => result.ChangeId).Should().Equal("a", "b", "c");
        withdrawn.Should().ContainSingle().Which.Should().Equal(prepared);
        _store.DidNotReceiveWithAnyArgs().Withdraw(default, default, default!, default);
    }

    /// <summary>
    /// A change call with no readable change is an error of the list. The changes that can run still run in one
    /// dispatch, the list holds nothing, and the error of the call comes first.
    /// </summary>
    [Fact]
    public async Task AnUnreadableChangeCallFailsTheListAndWithdrawsTheChangesThatRan()
    {
        var dispatches = 0;
        var prepared = new List<PendingAgentOperation>();
        var outcome = await new ChangeListCompiler(Registry).CompileAsync(UserId, [Change("a", "bulk_delete_habits")],
            new HashSet<Guid>(), (calls, token) =>
            {
                dispatches++;
                return FailsTheLogChange(prepared)(calls, token);
            }, _store, default, unreadable: [ChangeListCompiler.EmptyList("list")]);

        dispatches.Should().Be(1);
        outcome.Errors.Should().Equal(ChangeListCompiler.EmptyList("list"));
        outcome.Previews.Should().BeEmpty();
        var preview = prepared.Should().ContainSingle().Subject;
        _store.Received(1).Withdraw(UserId, preview.Id,
            AgentOperationFingerprint.Compute(preview.CapabilityId, _stored[preview.Id].Arguments.GetRawText()), null);
    }

    [Fact]
    public async Task AFailedChangeBesideThreeCorrectChangesHoldsNothingAndNamesOnlyThatChange()
    {
        var prepared = new List<PendingAgentOperation>();
        var outcome = await new ChangeListCompiler(Registry).CompileAsync(UserId,
            [Change("a", "bulk_delete_habits"), Change("b", "bulk_update_habits"), Change("c", "bulk_log_habits"), Change("d", "manage_subscription")],
            new HashSet<Guid>(), FailsTheLogChange(prepared), _store, default);

        outcome.IsSuccess.Should().BeFalse();
        outcome.Previews.Should().BeEmpty();
        outcome.Errors.Should().Equal(new ChangeError("c", string.Empty, "NOT_FOUND", "No matching habits found."));
        prepared.Should().HaveCount(3);
        foreach (var preview in prepared)
            _store.Received(1).Withdraw(UserId, preview.Id,
                AgentOperationFingerprint.Compute(preview.CapabilityId, _stored[preview.Id].Arguments.GetRawText()), null);
    }

    [Theory]
    [InlineData(AgentOperationStatus.Denied, "target_not_owned:bulk_update_habits:habit", null, "target_not_owned:bulk_update_habits:habit")]
    [InlineData(AgentOperationStatus.Failed, "Title is too long.", "VALIDATION", "VALIDATION")]
    [InlineData(AgentOperationStatus.Failed, "Title is too long.", null, "invalid_arguments")]
    public void ErrorOf_TakesTheErrorCodeOrThePolicyReason(AgentOperationStatus status, string reason, string? errorCode, string code)
    {
        var error = ChangeListCompiler.ErrorOf("x",
            new AgentOperationResult("bulk_update_habits", "bulk_update_habits", AgentRiskClass.Low, AgentConfirmationRequirement.None,
                status, PolicyReason: reason, ErrorCode: errorCode),
            new AiToolCallResult("bulk_update_habits", "x", false, null, null, "The write failed."), null);

        error.Should().Be(new ChangeError("x", string.Empty, code, "The write failed."));
    }

    [Fact]
    public void ErrorOf_AWriteThatTheHandlerRefusedTakesItsFailureKey() =>
        ChangeListCompiler.ErrorOf("x", null, new AiToolCallResult("bulk_update_habits", "x", false, null, null, "Repeated."),
            "repeated_single_entity").Code.Should().Be("repeated_single_entity");

    [Theory]
    [InlineData(4, 0, true)]
    [InlineData(3, 0, false)]
    [InlineData(2, 5, false)]
    [InlineData(3, 5, true)]
    [InlineData(3, 1, true)]
    [InlineData(2, 1, false)]
    public async Task APreviewBeyondTheThirdReturnsTooManyPreviews(int separate, int atomic, bool tooMany)
    {
        var changes = Enumerable.Range(0, separate).Select(index => Change("s" + index, "manage_subscription"))
            .Concat(Enumerable.Range(0, atomic).Select(index => Change("a" + index, index % 2 == 0 ? "bulk_delete_habits" : "bulk_update_habits")))
            .ToArray();

        var outcome = await new ChangeListCompiler(Registry).CompileAsync(UserId, changes, new HashSet<Guid>(),
            Prepares([]), _store, default);

        if (tooMany)
        {
            outcome.Errors.Should().ContainSingle().Which.Code.Should().Be(ChangeListCompiler.TooManyPreviews);
            outcome.Previews.Should().BeEmpty();
        }
        else
            outcome.Previews.Should().HaveCount(separate + atomic);
    }

    [Fact]
    public void Read_TheWrapperGivesEachChangeAndTheKindsShapeDropsThePlanTitle()
    {
        var wrapper = new AiToolCall(ChangeDeclarations.WrapperName, "list", JsonSerializer.SerializeToElement(new
        {
            changes = new object[] { new { id = "a", kind = "bulk_delete_habits", arguments = new { habit_ids = new[] { "x" } } }, new { kind = "manage_subscription", arguments = new { } } },
            plan_title = "Tidy"
        }));
        ChangeDeclarations.Read(wrapper, ChangeDeclarationShape.Wrapper).Select(change => (change.Id, change.Kind))
            .Should().Equal(("a", "bulk_delete_habits"), ("list/1", "manage_subscription"));
        ChangeDeclarations.PlanTitle([wrapper]).Should().Be("Tidy");

        var kind = new AiToolCall("bulk_delete_habits", "call", JsonSerializer.SerializeToElement(new { habit_ids = new[] { "x" }, plan_title = "Tidy" }));
        var change = ChangeDeclarations.Read(kind, ChangeDeclarationShape.Kinds).Should().ContainSingle().Subject;
        change.Id.Should().Be("call");
        change.Arguments.TryGetProperty(ChangeDeclarations.PlanTitleArgument, out _).Should().BeFalse();
    }

    [Fact]
    public void StepText_HoldsTheWholeTitlesThatFitIn40Characters()
    {
        PendingAgentOperation Preview(params string[] names) => new(Guid.NewGuid(), "c", "c", "c", AgentRiskClass.Low,
            AgentConfirmationRequirement.None, DateTime.UtcNow, Items: [.. names.Select(name => new PendingOperationItem("0", null, name, [], "s"))]);

        ProcessUserChatCommandHandler.StepText(Preview("Water - 710ml", "Water - 710ml")).Should().Be("Water - 710ml");
        ProcessUserChatCommandHandler.StepText(Preview("Read", "Walk the dog", "Meditate for ten minutes")).Should().Be("Read, Walk the dog");
        ProcessUserChatCommandHandler.StepText(Preview(new string('a', 50))).Should().Be(new string('a', 40));
        ProcessUserChatCommandHandler.StepText(Preview()).Should().BeEmpty();
    }
}
