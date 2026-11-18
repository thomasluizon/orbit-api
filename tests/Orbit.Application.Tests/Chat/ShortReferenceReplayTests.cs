using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Enums;
using Orbit.Application.Chat.References;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

/// <summary>
/// Short references at the chat boundary (thomasluizon/orbit-tickets#2154). The model reads a short reference for each
/// record and the normalizer gives each guard, tool and the executor the full GUID.
/// </summary>
public partial class ProcessUserChatCommandHandlerTests
{
    /// <summary>
    /// Run 4 of #2129 with no executor stub that refuses the cut id. The cut id resolves by its first 8 digits, so the
    /// rename reaches the executor with two full GUIDs of the person's habits.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task ShortReference_Run4CutIdResolvesByItsFirstEightDigits(string language)
    {
        var run = await RunDeniedAnswerTurnAsync(language, _ => [Text(language)], denyUnowned: false,
            first: children => Calls(Run4CutRename(children), Run4Delete(children)));

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        var rename = run.Held.Should().ContainSingle(request => request.OperationId == "bulk_update_habits").Subject;
        rename.Arguments.GetProperty("filter").GetProperty("habit_ids").EnumerateArray().Select(id => id.GetString())
            .Should().Equal(run.Children[0].Id.ToString(), run.Children[1].Id.ToString());
        run.Result.Value.PendingOperations.Should().NotBeNullOrEmpty();
    }

    private static string Reference(Guid id, int length = 8) => ReferenceCodes.Hex(id)[..length];

    /// <summary>The arguments of each request of one operation that reached the executor.</summary>
    private JsonElement[] Dispatched(string operation) => _operationExecutor.ReceivedCalls()
        .Where(call => call.GetMethodInfo().Name == nameof(IAgentOperationExecutor.ExecuteAsync))
        .Select(call => (AgentExecuteOperationRequest)call.GetArguments()[0]!)
        .Where(request => request.OperationId == operation).Select(request => request.Arguments).ToArray();

    /// <summary>The tool results that the model received in each continuation, as the provider adapter reads them.</summary>
    private List<IReadOnlyList<AiToolCallResult>> CaptureModelResults(string reply)
    {
        var results = new List<IReadOnlyList<AiToolCallResult>>();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Do<IReadOnlyList<AiToolCallResult>>(round => results.Add([.. round])), Arg.Any<Func<AiStreamEvent, Task>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = reply }));
        return results;
    }

    private static string ModelText(IEnumerable<AiToolCallResult> results) =>
        string.Join("\n", results.Select(result => JsonSerializer.Serialize(new { result.EntityId, result.Error, result.Payload })));

    /// <summary>The executor answers the read with its tool result, beside the writes that <see cref="HoldReplayWrites"/> holds.</summary>
    private void AnswerRead(string name, object payload) =>
        _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId == name), Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(new AgentOperationResult(name, name, AgentRiskClass.Low,
                AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded, Payload: payload)));

    private static Habit WithFixedId(Habit habit, Guid id)
    {
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(habit, id);
        return habit;
    }

    /// <summary>
    /// Water 1 and Water 6 share their first 12 hex digits. The Water 1 id cut short the run 4 way keeps 11 of them and
    /// then names no next digit of either habit, so the chat boundary cannot select one and turns it down. The cut id
    /// still holds the later groups of Water 1 only, so it is a copy of Water 1.
    /// </summary>
    private static readonly Guid CollidingWater1 = Guid.Parse("1b2c3d4e-5f60-4a1b-8c2d-3e4f5a6b7c8d");

    private static readonly Guid CollidingWater6 = Guid.Parse("1b2c3d4e-5f60-4f9e-9d8c-7b6a5f4e3d2c");

    private static Guid[] CollidingIds() => [CollidingWater1, .. Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()), CollidingWater6];

    /// <summary>
    /// A revision names its preview, its item and a field of <c>edits_json</c> by reference. The revision reaches the
    /// executor with the full id of each one.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task ShortReference_RevisionResolvesItsPreviewItsItemAndItsEditsJson(string language)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        var (morning, water) = (CreateHabit("Morning"), CreateHabit("Water"));
        SetupSelectionHabits(morning, water);
        var pendingId = Guid.NewGuid();
        var stored = new PendingAgentOperationExecution(pendingId, "habits.update", "update_habit",
            JsonSerializer.SerializeToElement(new { habit_id = water.Id, title = "Water" }), AgentExecutionSurface.Chat,
            AgentConfirmationRequirement.FreshConfirmation, "preview");
        _pendingOperationStore.GetAwaitingAsync(UserId, Arg.Any<CancellationToken>()).Returns([stored]);
        _pendingOperationStore.GetExecution(UserId, pendingId).Returns(stored);
        var handler = CreateHandler(ReadTool(ReviseTool, new ToolResult(true)));
        SetupAiResponse(ToolResponse(ReviseTool, "revise", JsonSerializer.Serialize(new
        {
            pending_operation_id = Reference(pendingId),
            items = new[] { new { item_id = Reference(water.Id), edits_json = JsonSerializer.Serialize(new { parent_habit_id = Reference(morning.Id) }) } },
            answer_question = false
        })));
        ReplyToReplay(language == "en" ? "Revised." : "Revisado.");

        var result = await handler.Handle(new(UserId, language == "en" ? "Put Water under Morning" : "Coloca Water dentro de Morning"), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var revision = Dispatched(ReviseTool).Should().ContainSingle().Subject;
        revision.GetProperty("pending_operation_id").GetGuid().Should().Be(pendingId);
        var item = revision.GetProperty("items")[0];
        item.GetProperty("item_id").GetString().Should().Be(water.Id.ToString());
        using var edits = JsonDocument.Parse(item.GetProperty("edits_json").GetString()!);
        edits.RootElement.GetProperty("parent_habit_id").GetGuid().Should().Be(morning.Id);
    }

    /// <summary>
    /// An <c>ask</c> pickList holds a <c>create_habits</c> item with a reference in its JSON string and a
    /// <c>log_habits</c> item with a reference in <c>habit_id</c>. The eval trace records the call with full GUIDs.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task ShortReference_PickListItemsResolveWithTheSchemaOfTheirOperation(string language)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        var (morning, water) = (CreateHabit("Morning"), CreateHabit("Water"));
        SetupSelectionHabits(morning, water);
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>()).Returns(new[] { FeatureFlagKeys.AiEvalTrace });
        var handler = CreateHandler();
        SetupAiResponse(ToolResponse("ask", "pick", JsonSerializer.Serialize(new
        {
            input = new
            {
                form = "pickList",
                missing_input = "bulk_selection",
                action = "log_habits",
                items = new object[]
                {
                    new { id = "stretch", label = "Stretch", arguments = JsonSerializer.Serialize(new { title = "Stretch", parent_habit_id = Reference(morning.Id) }) },
                    new { id = "water", label = "Water", arguments = new { habit_id = Reference(water.Id), date = "2026-04-03" } }
                }
            }
        })));
        ReplyToReplay(language == "en" ? "Choose." : "Escolha.");

        var result = await handler.Handle(new(UserId, language == "en" ? "Log my habits" : "Registra meus hábitos",
            EvalTraceRequested: true), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var items = EvalResponseJson(result.Value, false).GetProperty("evalTrace").GetProperty("toolCalls")[0]
            .GetProperty("arguments").GetProperty("input").GetProperty("items");
        using var created = JsonDocument.Parse(items[0].GetProperty("arguments").GetString()!);
        created.RootElement.GetProperty("parent_habit_id").GetGuid().Should().Be(morning.Id);
        items[1].GetProperty("arguments").GetProperty("habit_id").GetGuid().Should().Be(water.Id);
    }

    /// <summary>
    /// Two habits of the person share their first 8 digits. Model text names both by 12 digits, a reference with the next
    /// digits selects one, and a reference of 8 digits returns <c>unknown_reference</c> with both.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task ShortReference_CollisionShowsTwelveDigitsAndTheNextDigitsSelectOne(string language)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        _shortReferences = true;
        var water = WithFixedId(CreateHabit("Water"), Guid.Parse("dab2ad98-e891-4a8f-a8c6-91d0c3b2a1f4"));
        var twin = WithFixedId(CreateHabit("Water"), Guid.Parse("dab2ad98-77aa-4b1c-9d2e-3f4a5b6c7d8e"));
        SetupSelectionHabits(water, twin);
        _promptBuilder.BuildAccountContext(Arg.Any<PromptBuildRequest>())
            .Returns($"- \"Water\" (1 of 2) | {water.Id}\n- \"Water\" (2 of 2) | {twin.Id}");
        AiToolRequest? sent = null;
        _aiIntentService.SendWithToolsAsync(Arg.Do<AiToolRequest>(request => sent = request), Arg.Any<Func<AiStreamEvent, Task>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ConversationContext = TestConversationContext,
                ToolCalls =
                [
                    new("get_habit_metrics", "twelve", JsonSerializer.SerializeToElement(new { habit_id = Reference(twin.Id, 12) })),
                    new("get_habit_metrics", "eight", JsonSerializer.SerializeToElement(new { habit_id = "dab2ad98" }))
                ]
            }));
        var results = CaptureModelResults(language == "en" ? "Which one?" : "Qual deles?");
        var handler = CreateHandler(ReadTool("get_habit_metrics", new ToolResult(true, Payload: new { completions = 3 })));

        var result = await handler.Handle(new(UserId, language == "en" ? "Show Water metrics" : "Mostra as métricas de Water"), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        sent!.AccountContext.Should().StartWith("- \"Water\" (1 of 2) | dab2ad98e891\n- \"Water\" (2 of 2) | dab2ad9877aa")
            .And.NotContain(water.Id.ToString()).And.NotContain(twin.Id.ToString());
        Dispatched("get_habit_metrics").Should().ContainSingle().Which.GetProperty("habit_id").GetGuid().Should().Be(twin.Id);
        var unknown = results.Should().NotBeEmpty().And.Subject.First().Should().ContainSingle(item => item.Id == "eight").Subject;
        unknown.Success.Should().BeFalse();
        unknown.Error.Should().StartWith("unknown_reference: habit_id \"dab2ad98\" names no habit of this person.")
            .And.Contain("\"dab2ad98e891\" (Water)").And.Contain("\"dab2ad9877aa\" (Water)").And.NotContain(water.Id.ToString());
    }

    /// <summary>
    /// The digits match only a habit of another person, and a habit reference sits in <c>goal_id</c>. Neither resolves,
    /// and the error holds no data of the other person.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task ShortReference_AForeignPrefixAndAReferenceOfAnotherTypeDoNotResolve(string language)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        var water = CreateHabit("Water");
        var foreign = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, Guid.NewGuid(), "Secret plan", null, null,
            DueDate: Today)).Value;
        SetupSelectionHabits(water, foreign);
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls =
            [
                new("get_habit_metrics", "foreign", JsonSerializer.SerializeToElement(new { habit_id = Reference(foreign.Id) })),
                new("review_goals", "goal", JsonSerializer.SerializeToElement(new { goal_id = Reference(water.Id) }))
            ]
        });
        var results = CaptureModelResults(language == "en" ? "Not found." : "Não encontrei.");
        var handler = CreateHandler(ReadTool("get_habit_metrics", new ToolResult(true)), ReadTool("review_goals", new ToolResult(true)));

        var result = await handler.Handle(new(UserId, language == "en" ? "Show it" : "Mostra"), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        Dispatched("get_habit_metrics").Should().BeEmpty();
        Dispatched("review_goals").Should().BeEmpty();
        var round = results.Should().NotBeEmpty().And.Subject.First();
        round.Should().OnlyContain(item => !item.Success && item.Error!.StartsWith("unknown_reference:"));
        ModelText(round).Should().NotContain("Secret plan").And.NotContain(foreign.Id.ToString());
        round.Single(item => item.Id == "goal").Error.Should().Contain("names no goal of this person");
    }

    /// <summary>
    /// A full GUID of a habit of the person resolves as itself with the setting on and off. A full GUID of no habit of
    /// the person does not resolve.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShortReference_AFullGuidResolvesAsItselfOnlyForARecordOfThePerson(bool shortReferences)
    {
        SetupUserAndPayGate();
        _shortReferences = shortReferences;
        var water = CreateHabit("Water");
        SetupSelectionHabits(water);
        var nobody = Guid.NewGuid();
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls =
            [
                new("get_habit_metrics", "owned", JsonSerializer.SerializeToElement(new { habit_id = water.Id })),
                new("get_habit_metrics", "nobody", JsonSerializer.SerializeToElement(new { habit_id = nobody }))
            ]
        });
        var results = CaptureModelResults("Done.");
        var handler = CreateHandler(ReadTool("get_habit_metrics", new ToolResult(true)));

        var result = await handler.Handle(new(UserId, "Show Water metrics"), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        Dispatched("get_habit_metrics").Should().ContainSingle().Which.GetProperty("habit_id").GetGuid().Should().Be(water.Id);
        results.Should().NotBeEmpty().And.Subject.First().Single(item => item.Id == "nobody").Error
            .Should().StartWith($"unknown_reference: habit_id \"{nobody}\" names no habit of this person.");
    }

    /// <summary>
    /// A rename whose reference names no habit beside a prepared delete. The turn offers one correction, as it does for
    /// a write that the ownership check turns down, and a turn whose correction fails leaves no preview.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task ShortReference_AnUnknownReferenceBesideAPreparedWriteGetsOneCorrectionOrNoPreview(string language)
    {
        var failed = await RunRequestTurnAsync(language, RequestMessage(language),
            children => Calls(Run4Delete(children), Rename("rename-one", "0f0f0f0f")), _ => [Text(language), Text(language)]);

        failed.Result.IsSuccess.Should().BeTrue(failed.Result.Error);
        var denied = failed.ToolResults[0].Should().ContainSingle(result => !result.Success).Subject;
        denied.Error.Should().StartWith("unknown_reference: habit_id \"0f0f0f0f\" names no habit of this person.");
        JsonSerializer.Serialize(denied.Payload).Should().Contain("correction_allowed");
        failed.Result.Value.PendingOperations.Should().BeNullOrEmpty("a preview with only the delete holds half of the request");
        failed.Result.Value.AiMessage.Should().Be(NotPreparedFor(language));
        failed.Held.Should().NotContain(request => request.OperationId == "update_habit", "the boundary turns the rename down before dispatch");

        var corrected = await RunRequestTurnAsync(language, RequestMessage(language),
            children => Calls(Run4Delete(children), Rename("rename-one", OneCharacterOff(children[1].Id))),
            children => [Calls(Rename("rename-one-again", Reference(children[1].Id))), Text(language)]);

        corrected.Result.IsSuccess.Should().BeTrue(corrected.Result.Error);
        corrected.Result.Value.PendingOperations!.SelectMany(operation => operation.Items!).Select(item => item.EntityId)
            .Should().BeEquivalentTo([.. corrected.Children[4..].Select(child => child.Id), corrected.Children[1].Id]);
        corrected.Result.Value.PolicyDenials.Should().BeNullOrEmpty();
    }

    /// <summary>
    /// One turn reads a habit and prepares a write on it by reference. The model text holds references, and a GUID of no
    /// listed type stays at full length. The client blocks, the stored record and the eval trace hold full GUIDs.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task ShortReference_ModelTextHoldsReferencesAndEveryOtherReaderHoldsFullGuids(string language)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        _shortReferences = true;
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new[] { FeatureFlagKeys.AiEvalTrace, FeatureFlagKeys.AiConversationRecording });
        var water = CreateHabit("Water");
        SetupSelectionHabits(water);
        var suggestion = Guid.NewGuid();
        var payload = new { habits = new[] { new { id = water.Id, title = "Water" } }, suggestion_id = suggestion };
        var read = ReadTool("query_habits", new ToolResult(true, Payload: payload));
        var rename = new UpdateHabitTool(_habitRepo, _userDateService, _mediator);
        var handler = CreateHandler(read, rename);
        var held = HoldReplayWrites(rename);
        AnswerRead(read.Name, payload);
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls =
            [
                new("query_habits", "read", JsonSerializer.SerializeToElement(new { search = "Water" })),
                new("update_habit", "write", JsonSerializer.SerializeToElement(new { habit_id = Reference(water.Id), title = "Water - 1L" }))
            ]
        });
        var results = CaptureModelResults(language == "en" ? "Review the change." : "Revise a mudança.");
        ConversationTurnCapture? stored = null;
        _conversationRecorder.RecordAsync(Arg.Do<ConversationTurnCapture>(turn => stored = turn), Arg.Any<CancellationToken>()).Returns(true);

        var result = await handler.Handle(new(UserId, language == "en" ? "Rename Water" : "Renomeia Water", EvalTraceRequested: true,
            ClientContext: new(SupportsPendingOperationChanges: true, ConversationId: Guid.NewGuid())), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        held.Should().ContainSingle(request => request.OperationId == rename.Name).Which.Arguments.GetProperty("habit_id").GetGuid().Should().Be(water.Id);
        var modelText = ModelText(results.SelectMany(round => round));
        modelText.Should().Contain(Reference(water.Id)).And.NotContain(water.Id.ToString()).And.Contain(suggestion.ToString());
        result.Value.PendingOperations.Should().ContainSingle().Which.Items!.Single().EntityId.Should().Be(water.Id);
        stored.Should().NotBeNull();
        stored!.ToolCallsJson.Should().Contain(water.Id.ToString()).And.NotContain($"\"{Reference(water.Id)}\"");
        var trace = EvalResponseJson(result.Value, false).GetProperty("evalTrace").GetRawText();
        trace.Should().Contain(water.Id.ToString()).And.NotContain($"\"{Reference(water.Id)}\"");
    }

    /// <summary>
    /// With the setting off, model text holds full GUIDs, and the same calls with full GUIDs give the same previews as
    /// with the setting on.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task ShortReference_SettingOffShowsFullGuidsAndGivesTheSamePreviews(string language)
    {
        var water = WithFixedId(CreateHabit("Water"), Guid.Parse("4c1e7f20-3b5a-4d6c-8e9f-0a1b2c3d4e5f"));
        async Task<(ChatResponse Response, string ModelText)> RunAsync(bool shortReferences)
        {
            _shortReferences = shortReferences;
            var user = User.Create("Reader", "reader@example.test").Value;
            user.SetLanguage(language);
            SetupUserAndPayGate(user);
            SetupSelectionHabits(water);
            var payload = new { habits = new[] { new { id = water.Id, title = "Water" } } };
            var read = ReadTool("query_habits", new ToolResult(true, Payload: payload));
            var rename = new UpdateHabitTool(_habitRepo, _userDateService, _mediator);
            var handler = CreateHandler(read, rename);
            HoldReplayWrites(rename);
            AnswerRead(read.Name, payload);
            SetupAiResponse(new AiResponse
            {
                ConversationContext = TestConversationContext,
                ToolCalls =
                [
                    new("query_habits", "read", JsonSerializer.SerializeToElement(new { search = "Water" })),
                    new("update_habit", "write", JsonSerializer.SerializeToElement(new { habit_id = water.Id, title = "Water - 1L" }))
                ]
            });
            var results = CaptureModelResults(language == "en" ? "Review the change." : "Revise a mudança.");
            var result = await handler.Handle(new(UserId, language == "en" ? "Rename Water" : "Renomeia Water",
                ClientContext: new(SupportsPendingOperationChanges: true)), default);
            result.IsSuccess.Should().BeTrue(result.Error);
            return (result.Value, ModelText(results.SelectMany(round => round)));
        }

        var off = await RunAsync(shortReferences: false);
        var on = await RunAsync(shortReferences: true);

        off.ModelText.Should().Contain("4c1e7f20-3b5a-4d6c-8e9f-0a1b2c3d4e5f");
        on.ModelText.Should().Contain("\"4c1e7f20\"").And.NotContain("4c1e7f20-3b5a-4d6c-8e9f-0a1b2c3d4e5f");
        static object Shape(ChatResponse response) => response.PendingOperations!.Select(operation => new
        {
            operation.CapabilityId,
            operation.PreviewFingerprint,
            Items = operation.Items!.Select(item => new { item.ItemId, item.EntityId, item.EntityName, Fields = item.Fields.Select(field => new { field.Field, field.NewValue }) })
        }).ToArray();
        JsonSerializer.Serialize(Shape(on.Response)).Should().Be(JsonSerializer.Serialize(Shape(off.Response)));
    }

    /// <summary>
    /// The book read the skips of the person before the turn made a skip. A later call of the same turn names that skip
    /// by its full id or by its reference. The write makes the book read the skips again, so the model text shows the
    /// reference of the new skip and the undo reaches the executor with its full id.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShortReference_ARecordThatTheTurnMadeResolvesInALaterCallOfTheTurn(bool byReference)
    {
        SetupUserAndPayGate();
        _shortReferences = true;
        var water = CreateHabit("Water");
        SetupSelectionHabits(water);
        _promptBuilder.BuildAccountContext(Arg.Any<PromptBuildRequest>())
            .Returns($"- \"Water\" | {water.Id}\n- calendar event | {Guid.NewGuid()}");
        var skips = new List<HabitSkipUndo>();
        _skipRepo.ProjectAsync(Arg.Any<Expression<Func<HabitSkipUndo, bool>>>(),
                Arg.Any<Func<IQueryable<HabitSkipUndo>, IQueryable<ReferenceRecord>>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<ReferenceRecord>)skips.Where(call.ArgAt<Expression<Func<HabitSkipUndo, bool>>>(0).Compile())
                .Select(skip => new ReferenceRecord(skip.Id, "skip")).ToArray());
        var skipId = Guid.NewGuid();
        var skip = WriteTool("skip_habit", () =>
        {
            skips.Add(HabitSkipUndo.Create(skipId, water).Value);
            return new ToolResult(true, EntityId: water.Id.ToString(), EntityName: water.Title, Payload: new { skip_id = skipId });
        });
        var undo = WriteTool("undo_skip_habit", () => new ToolResult(true, EntityId: water.Id.ToString()));
        var handler = CreateHandler(skip, undo);
        SetupAiResponse(ToolResponse("skip_habit", "skip", JsonSerializer.Serialize(new { habit_id = Reference(water.Id) })));
        var results = new List<IReadOnlyList<AiToolCallResult>>();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Do<IReadOnlyList<AiToolCallResult>>(round => results.Add([.. round])), Arg.Any<Func<AiStreamEvent, Task>?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                Result.Success(ToolResponse("undo_skip_habit", "undo", JsonSerializer.Serialize(new
                {
                    habit_id = Reference(water.Id),
                    skip_id = byReference ? Reference(skipId) : skipId.ToString()
                }))),
                Result.Success(new AiResponse { TextMessage = "Done." }));

        var result = await handler.Handle(new(UserId, "Skip Water, then undo the skip"), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        Dispatched("undo_skip_habit").Should().ContainSingle().Which.GetProperty("skip_id").GetGuid().Should().Be(skipId);
        results.Should().HaveCount(2);
        ModelText(results[0]).Should().Contain($"\"{Reference(skipId)}\"").And.NotContain(skipId.ToString());
        results[1].Should().ContainSingle(item => item.Id == "undo").Which.Success.Should().BeTrue();
    }

    /// <summary>
    /// A record selection over two same-titled groups names each habit by the reference that the model reads, so the
    /// server cannot complete the card from one group. The issued card holds the full id of each habit. The answer in the
    /// next turn settles the chosen habit, and its delete reaches a preview with no second ask.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task ShortReference_RecordSelectionOptionsNameTheirHabitsSoTheAnswerSettlesTheChosenOne(string language)
    {
        var portuguese = language == "pt-BR";
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        _shortReferences = true;
        EnableComposition();
        _unitOfWork.PassThroughTransactions<Result<PreparedAiBlockAnswer>>();
        var (walk, water) = portuguese ? ("Caminhada", "Água") : ("Walk", "Water");
        Habit[] habits = [CreateHabit(walk), CreateHabit(walk), CreateHabit(water), CreateHabit(water)];
        SetupSelectionHabits(habits);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var delete = new DeleteHabitTool(_mediator, _habitRepo);
        var (askHandler, answerHandler) = (CreateHandler(query, delete, RealAskTool()), CreateHandler(query, delete, RealAskTool()));
        var held = HoldSelectionReplayWrites(query, delete);
        var issued = new List<IssuedAiBlock>();
        _blockStore.AddAsync(Arg.Do<IssuedAiBlock>(issued.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = [new(query.Name, "lookup", JsonSerializer.SerializeToElement(new { orbit_read_complete = false }))]
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ToolResponse("ask", "ask", JsonSerializer.Serialize(new
            {
                input = new
                {
                    missing_input = "record_selection",
                    form = "choice",
                    multiSelect = false,
                    question = portuguese ? "Qual hábito devo excluir?" : "Which habit should I delete?",
                    options = habits.Select((habit, index) => new { id = Reference(habit.Id), label = $"{habit.Title} {index + 1}" }).ToArray()
                }
            }))), Result.Success(new AiResponse { TextMessage = portuguese ? "Qual?" : "Which one?" }));

        var asked = await askHandler.Handle(new ProcessUserChatCommand(UserId,
            portuguese ? "Exclui um dos meus hábitos repetidos" : "Delete one of my repeated habits",
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, PartKinds: ["choice"])), default);

        asked.IsSuccess.Should().BeTrue(asked.Error);
        var card = issued.Should().ContainSingle("the card reached the person").Subject.ReadDefinition();
        card.Options.Select(option => option.Id).Should().Equal(habits.Select(habit => habit.Id.ToString()),
            "the issued card names each habit by its full id");

        issued.Clear();
        var answered = StoreAnsweredBlock(card);
        AiToolRequest? sent = null;
        _aiIntentService.SendWithToolsAsync(Arg.Do<AiToolRequest>(value => sent = value),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ToolResponse(delete.Name, "delete",
                JsonSerializer.Serialize(new { habit_id = Reference(habits[0].Id), orbit_write_complete = true }))));
        ReplyToReplay(portuguese ? "Revise a exclusão." : "Review the delete.");

        var result = await answerHandler.Handle(new(UserId, "Answer", BlockAnswer: new(answered.Id, [card.Options[0].Id]),
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, PartKinds: ["choice"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        sent!.DynamicContext.Should().Contain("Validated choice selection: ")
            .And.Contain($"\"chosenTargets\":[\"{Reference(habits[0].Id)}\"]");
        held.Should().ContainSingle(request => request.OperationId == delete.Name)
            .Which.Arguments.GetProperty("habit_id").GetGuid().Should().Be(habits[0].Id);
        issued.Should().BeEmpty("the answer settles the chosen habit, so the server asks no second time");
        result.Value.PendingOperations!.SelectMany(operation => operation.Items!).Select(item => item.EntityId)
            .Should().Equal([habits[0].Id]);
    }

    /// <summary>
    /// A pickList item id and an option id of an ask card are free labels when they name no habit of the person. A
    /// <c>YYYY-MM-DD</c> date starts with 8 hexadecimal digits, and it reaches the issued card unchanged.
    /// </summary>
    [Theory]
    [InlineData("en", "pickList")]
    [InlineData("pt-BR", "pickList")]
    [InlineData("en", "choice")]
    [InlineData("pt-BR", "choice")]
    public async Task ShortReference_AskDateIdIsAFreeLabelAndReachesTheCardUnchanged(string language, string form)
    {
        var portuguese = language == "pt-BR";
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        _shortReferences = true;
        EnableComposition();
        SetupSelectionHabits(CreateHabit(portuguese ? "Água" : "Water"));
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var title = portuguese ? "Ler" : "Read";
        object input = form == "pickList"
            ? new
            {
                form,
                missing_input = "habit_setup",
                title,
                action = "create_habits",
                items = new[] { new { id = "2026-11-18", label = title, arguments = JsonSerializer.Serialize(new { title }) } }
            }
            : new
            {
                form,
                missing_input = "preference",
                question = portuguese ? "Quando começo?" : "When do I start?",
                options = new[] { new { id = "2026-11-18", label = portuguese ? "Hoje" : "Today" }, new { id = "2026-11-19", label = portuguese ? "Amanhã" : "Tomorrow" } }
            };
        var issued = new List<IssuedAiBlock>();
        _blockStore.AddAsync(Arg.Do<IssuedAiBlock>(issued.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var results = CaptureModelResults(portuguese ? "Escolha." : "Choose.");
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = [AskSelectionRead(), new("ask", "ask", JsonSerializer.SerializeToElement(new { input }))]
        });
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(RealAskTool(), query);
        UseProductionReadExecutor(query);

        var result = await handler.Handle(new(UserId, portuguese ? "Prepare a seleção pedida." : "Prepare the requested selection.",
            ClientContext: new(SupportsParts: true, PartKinds: [form], Locale: language)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        results.SelectMany(round => round).Should().NotContain(item => item.Error != null && item.Error.Contains("unknown_reference"));
        var card = issued.Should().ContainSingle("the card reached the person").Subject.ReadDefinition();
        if (form == "pickList")
            card.Items.Select(item => item.Id).Should().Equal(["2026-11-18"], "an item id is a free label");
        else
            card.Options.Select(option => option.Id).Should().Equal(["2026-11-18", "2026-11-19"], "an option id that names no habit is a free label");
    }

    private static IAiTool WriteTool(string name, Func<ToolResult> execute)
    {
        var tool = Substitute.For<IAiTool>();
        tool.Name.Returns(name);
        tool.Description.Returns(name);
        tool.IsReadOnly.Returns(false);
        tool.GetParameterSchema().Returns(new { type = "object" });
        tool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>()).Returns(_ => execute());
        return tool;
    }

    /// <summary>The rename that the model sent in run 4 of #2129, with the second id cut short.</summary>
    private static AiToolCall Run4CutRename(Habit[] children) => new("bulk_update_habits", "rename", JsonSerializer.SerializeToElement(new
    {
        filter = new { all = false, habit_ids = new[] { children[0].Id.ToString(), CutShort(children[1].Id) }, is_completed = false },
        updates = new { title = RenameTitle }
    }));

    /// <summary>
    /// The person owns these records of a type that the turn context does not read, so a reference to one resolves at the
    /// chat boundary. The repository answers the projection that the handler reads.
    /// </summary>
    private static void OwnRecords<T>(IGenericRepository<T> repository, Func<T, ReferenceRecord> record, params T[] records) where T : Entity =>
        repository.ProjectAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<Func<IQueryable<T>, IQueryable<ReferenceRecord>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<ReferenceRecord>)records.Where(call.ArgAt<Expression<Func<T, bool>>>(0).Compile())
                .Select(record).ToArray());
}
