using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    private const string OwnerParentRequest = "Post lunch: take vitamin D (once a week) Before bed: take rovustatina (everyday)";

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task ExistingParents_SingleCreateApprovalSavesThePreviewedChild(string language)
    {
        var parents = ParentAccount(language);
        ScriptToolCall("""{"title":"Take vitamin D","frequency_unit":"Week","frequency_quantity":1,"due_time":"13:00","emoji":"💊"}""");
        var held = await CreateHandler().Handle(new(_userId, "Post lunch: take vitamin D (once a week)"), default);
        var pending = held.Value.PendingOperations.Should().ContainSingle().Which;
        pending.Items!.Single().Fields.Should().Contain(field => field.Field == "parent_habit_id" && field.NewValue == "Post Lunch");
        pending.Items!.Single().FieldOptions.Should().BeNull();

        var execution = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;
        var approved = await _executor.ExecuteAsync(new(_userId, execution.OperationId, execution.Arguments,
            execution.Surface, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        await _habitRepo.Received(1).AddAsync(Arg.Is<Habit>(habit => habit.ParentHabitId == parents[0].Id
            && habit.Title == "Take vitamin D" && habit.FrequencyUnit == FrequencyUnit.Week
            && habit.FrequencyQuantity == 1 && habit.DueTime == new TimeOnly(13, 0) && habit.Emoji == "💊"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", "Create habits: Read and Stretch every day.")]
    [InlineData("pt-BR", "Crie hábitos: Read e Stretch todo dia.")]
    [InlineData("en", "Unknown routine: Read and Stretch every day.")]
    public async Task ExistingParents_UnmatchedHeadingDefaultsToAnApprovableRoot(string language, string message)
    {
        ParentAccount(language);
        ScriptToolCall("""{"habits":[{"title":"Read","frequency_unit":"Day","frequency_quantity":1},{"title":"Stretch","frequency_unit":"Day","frequency_quantity":1}]}""", "bulk_create_habits");
        var held = await CreateHandler().Handle(new(_userId, message), default);
        var pending = held.Value.PendingOperations.Should().ContainSingle().Which;

        pending.Items.Should().OnlyContain(item => item.ValidationErrors == null || item.ValidationErrors.Count == 0);
        var revisionService = ParentRevisionService();
        (await revisionService.CanApproveAsync(_userId, _pendingOperationStore.GetExecution(_userId, pending.Id)!, default)).Should().BeTrue();
        pending.Items.Should().OnlyContain(item => item.Fields.Any(field => field.Field == "parent_habit_id"
            && field.ProposedValue.HasValue && field.ProposedValue.Value.ValueKind == JsonValueKind.Null && field.IsEditable));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingParents_PersistedIdentityUsesEachItemsEffectiveParent(bool differentParent)
    {
        var parents = ParentAccount("en");
        ScriptToolCall(JsonSerializer.Serialize(new { habits = new[] { new { title = "Take vitamin D", parent_habit_id = parents[0].Id, frequency_unit = "Week" } } }), "bulk_create_habits");
        var handler = CreateHandler();
        var first = await handler.Handle(new(_userId, "Post lunch: take vitamin D (once a week)"), default);
        var pending = first.Value.PendingOperations.Should().ContainSingle().Which;
        var nextParent = parents[differentParent ? 1 : 0];
        ScriptToolCall(JsonSerializer.Serialize(new { habits = new[] { new { title = "Take vitamin D", parent_habit_id = nextParent.Id, frequency_unit = "Day" } } }), "bulk_create_habits");

        var second = await handler.Handle(new(_userId, $"{nextParent.Title}: take vitamin D (everyday)",
            History: [new ChatHistoryMessage("user", "Post lunch: take vitamin D (once a week)")]), default);

        _pendingOperationStore.GetPendingHabitCreations(_userId).Should().Contain(item => item.ParentHabitId == parents[0].Id);
        second.Value.PendingOperations.Should().HaveCount(differentParent ? 1 : 0);
        _pendingOperationStore.GetExecution(_userId, pending.Id).Should().NotBeNull();
    }

    [Fact]
    public async Task ExistingParents_SupersessionUsesItemParentBeforeSharedParent()
    {
        var parents = ParentAccount("en");
        ScriptToolCall(JsonSerializer.Serialize(new { parent_habit_id = parents[1].Id, habits = new[] { new { title = "Take vitamin D", parent_habit_id = parents[0].Id, frequency_unit = "Week" } } }), "bulk_create_habits");
        var first = await CreateHandler().Handle(new(_userId, "Create a child under Before Bed. Post lunch: take vitamin D (once a week)"), default);
        var pending = first.Value.PendingOperations.Should().ContainSingle().Which;

        _pendingOperationStore.SupersedeHabitCreation(_userId, "Take vitamin D", parents[0].Id).Should().BeTrue();
        _pendingOperationStore.GetExecution(_userId, pending.Id).Should().BeNull();
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", false)]
    [InlineData("pt-BR", true)]
    public async Task ExistingParents_ConfirmationOnlyReturnsTheActualRootOrChildPreview(string language, bool children)
    {
        ParentAccount(language);
        ScriptParentBatch();
        var handler = CreateHandler();
        var first = await handler.Handle(new(_userId, children ? OwnerParentRequest : "Create two habits, take vitamin D weekly and take rovustatina daily."), default);
        var pending = first.Value.PendingOperations.Should().ContainSingle().Which;
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Yes, they will be sub-habits of Before Bed and Post Lunch. Nothing saved." }));
        var events = new List<ChatStreamEvent>();

        var reply = await handler.Handle(new(_userId, language == "en"
            ? "will they be sub-habits of Before Bed and Post Lunch?"
            : "eles serão sub-hábitos de Before Bed e Post Lunch?", StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        var actual = reply.Value.PendingOperations.Should().ContainSingle().Which;
        actual.Id.Should().Be(pending.Id);
        JsonElement.DeepEquals(JsonSerializer.SerializeToElement(actual.Items), JsonSerializer.SerializeToElement(pending.Items)).Should().BeTrue();
        reply.Value.AiMessage.Should().NotBeNullOrWhiteSpace();
        foreach (var item in actual.Items!)
            reply.Value.AiMessage.Should().Contain(item.EntityName);
        reply.Value.Parts.Should().BeNullOrEmpty();
        events.Where(item => item.Type == "delta").Should().ContainSingle().Which.Text.Should().Be(reply.Value.AiMessage);
        actual.Items!.SelectMany(item => item.Fields).Count(field => field.Field == "parent_habit_id"
            && field.ProposedValue is { ValueKind: JsonValueKind.String }).Should().Be(children ? 2 : 0);
    }

    [Theory]
    [InlineData("en", "fix it")]
    [InlineData("en", "make them sub-habits")]
    [InlineData("pt-BR", "corrige")]
    [InlineData("pt-BR", "então corrige")]
    public async Task ExistingParents_CorrectionReplacesPreviewPreservingAllOtherArguments(string language, string correction)
    {
        var parents = ParentAccount(language);
        ScriptParentBatch();
        var handler = CreateHandler();
        var first = await handler.Handle(new(_userId, "Create two habits, take vitamin D weekly and take rovustatina daily."), default);
        var pending = first.Value.PendingOperations.Should().ContainSingle().Which;
        var before = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var events = new List<ChatStreamEvent>();

        var reply = await handler.Handle(new(_userId, correction,
            History: [new("user", OwnerParentRequest), new("user", "will they be sub-habits of Before Bed and Post Lunch?")],
            ClientContext: new AgentClientContext(SupportsParts: true),
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        reply.IsSuccess.Should().BeTrue();
        var revised = reply.Value.PendingOperations.Should().ContainSingle().Which;
        revised.Id.Should().NotBe(pending.Id);
        _pendingOperationStore.GetExecution(_userId, pending.Id).Should().BeNull();
        _pendingOperationStore.Confirm(_userId, pending.Id).Should().BeNull();
        var after = _pendingOperationStore.GetExecution(_userId, revised.Id)!;
        for (var index = 0; index < 2; index++)
        {
            var item = after.Arguments.GetProperty("habits")[index];
            item.GetProperty("parent_habit_id").GetGuid().Should().Be(parents[index].Id);
            foreach (var property in before.Arguments.GetProperty("habits")[index].EnumerateObject())
                item.GetProperty(property.Name).GetRawText().Should().Be(property.Value.GetRawText());
        }
        reply.Value.AiMessage.Should().BeNullOrEmpty();
        reply.Value.Parts.Should().BeEmpty();
        events.Should().NotContain(item => item.Type == "delta");
        var confirmation = _pendingOperationStore.Confirm(_userId, revised.Id)!;
        var approved = await _executor.ExecuteAsync(new(_userId, after.OperationId, after.Arguments,
            after.Surface, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));
        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        await _habitRepo.Received(1).AddAsync(Arg.Is<Habit>(habit => habit.Title == "Take vitamin D"
            && habit.ParentHabitId == parents[0].Id && habit.Description == "Keep with food"
            && habit.FrequencyUnit == FrequencyUnit.Week && habit.DueTime == new TimeOnly(13, 0)), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", true)]
    public async Task ExistingParents_StreamingNeverEmitsNarrativeAroundThePreview(string language, bool parts)
    {
        ParentAccount(language);
        ScriptParentBatch();
        var toolResponse = await _aiIntentService.SendWithToolsAsync(default!, null, default);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (call.ArgAt<Func<AiStreamEvent, Task>?>(1) is { } sink)
                    await sink(AiStreamEvent.Delta("I will create two top-level habits.\n"));
                return toolResponse;
            });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                if (call.ArgAt<Func<AiStreamEvent, Task>?>(2) is { } sink)
                    await sink(AiStreamEvent.Delta("Yes, children. Proposal: nothing saved.\n"));
                return Result.Success(new AiResponse { TextMessage = "Yes, children. Proposal: nothing saved." });
            });
        var events = new List<ChatStreamEvent>();

        var result = await CreateHandler().Handle(new(_userId, OwnerParentRequest,
            ClientContext: new AgentClientContext(SupportsParts: parts), StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.Value.PendingOperations.Should().ContainSingle();
        if (parts) result.Value.AiMessage.Should().BeNullOrEmpty();
        else result.Value.AiMessage.Should().Contain("approving").And.NotContain("top-level").And.NotContain("nothing saved");
        result.Value.Parts.Should().BeNullOrEmpty();
        if (parts) events.Should().NotContain(item => item.Type == "delta");
        else events.Where(item => item.Type == "delta").Should().ContainSingle().Which.Text.Should().Be(result.Value.AiMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingParents_AmbiguousHeadingOffersCandidatesAndAcceptsOnlyAnOfferedChoice(bool noParent)
    {
        var parents = ParentAccount("en");
        var duplicate = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, "Post Lunch", FrequencyUnit.Day, 1, Today)).Value;
        var unrelated = Enumerable.Range(0, 40).Select(index => Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            _userId, $"Other routine {index}", FrequencyUnit.Day, 1, Today)).Value).ToArray();
        var account = parents.Concat([duplicate]).Concat(unrelated).ToArray();
        UseParentHabits(account);
        ScriptToolCall("""{"habits":[{"title":"Take vitamin D","frequency_unit":"Week","frequency_quantity":1}]}""", "bulk_create_habits");
        var held = await CreateHandler().Handle(new(_userId, "Post lunch: take vitamin D (once a week)"), default);
        var pending = held.Value.PendingOperations.Should().ContainSingle().Which;
        var item = pending.Items.Should().ContainSingle().Which;
        var json = JsonSerializer.SerializeToElement(item, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var choices = json.GetProperty("fieldOptions")[0];
        choices.GetProperty("field").GetString().Should().Be("parent_habit_id");
        choices.GetProperty("required").GetBoolean().Should().BeTrue();
        choices.GetProperty("options").EnumerateArray().Select(option => option.GetProperty("value").ToString())
            .Should().BeEquivalentTo(parents[0].Id.ToString(), duplicate.Id.ToString(), "");
        var service = ParentRevisionService();
        _habitRepo.ClearReceivedCalls();
        (await service.CanApproveAsync(_userId, _pendingOperationStore.GetExecution(_userId, pending.Id)!, default)).Should().BeFalse();
        var queries = _habitRepo.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "FindAsync").ToArray();
        queries.Should().ContainSingle();
        var predicate = (Expression<Func<Habit, bool>>)queries[0].GetArguments()[0]!;
        account.Where(predicate.Compile()).Select(habit => habit.Id).Should().BeEquivalentTo([parents[0].Id, duplicate.Id]);
        var rejected = await service.ReviseAsync(_userId, pending.Id, new(pending.PreviewFingerprint!,
            [new(item.ItemId, JsonSerializer.SerializeToElement(new { parent_habit_id = parents[1].Id }))]), default);
        rejected.IsSuccess.Should().BeFalse();
        rejected.Error.Should().Be("option_not_offered");

        var accepted = await service.ReviseAsync(_userId, pending.Id, new(pending.PreviewFingerprint!,
            [new(item.ItemId, JsonSerializer.SerializeToElement(new { parent_habit_id = noParent ? null : (string?)duplicate.Id.ToString() }))]), default);

        accepted.IsSuccess.Should().BeTrue();
        accepted.Preview!.Items!.Single().FieldOptions.Should().BeNull();
        (await service.CanApproveAsync(_userId, _pendingOperationStore.GetExecution(_userId, pending.Id)!, default)).Should().BeTrue();
        accepted.Preview!.Items!.Single().Fields.Should().Contain(field => field.Field == "parent_habit_id"
            && field.NewValue == (noParent ? "No parent" : "Post Lunch"));
        if (!noParent)
        {
            var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
            var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;
            var approved = await _executor.ExecuteAsync(new(_userId, stored.OperationId, stored.Arguments,
                stored.Surface, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));
            approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
            await _habitRepo.Received(1).AddAsync(Arg.Is<Habit>(habit => habit.ParentHabitId == duplicate.Id), Arg.Any<CancellationToken>());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingParents_FreshnessTracksOnlyBoundOrCandidateParents(bool ambiguous)
    {
        var parents = ParentAccount("en");
        var candidate = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId,
            ambiguous ? "Post Lunch" : "Another routine", FrequencyUnit.Day, 1, Today)).Value;
        UseParentHabits([.. parents, candidate]);
        ScriptToolCall("""{"habits":[{"title":"Take vitamin D","frequency_unit":"Week","frequency_quantity":1}]}""", "bulk_create_habits");
        var held = await CreateHandler().Handle(new(_userId, "Post lunch: take vitamin D (once a week)"), default);
        var pending = held.Value.PendingOperations.Should().ContainSingle().Which;
        var execution = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var service = ParentRevisionService();
        (await service.IsCurrentAsync(_userId, execution, default)).Should().BeTrue();

        parents[1].Update(new HabitUpdateParams(HabitSchedule.Recurring, "Unrelated routine renamed", parents[1].Description,
            parents[1].FrequencyUnit, parents[1].FrequencyQuantity, parents[1].Days.ToArray(), parents[1].IsBadHabit, parents[1].NextDueDate))
            .IsSuccess.Should().BeTrue();
        (await service.IsCurrentAsync(_userId, execution, default)).Should().BeTrue();

        var dependency = ambiguous ? candidate : parents[0];
        dependency.Update(new HabitUpdateParams(dependency.Schedule, dependency.Title, "Changed parent description",
            dependency.FrequencyUnit, dependency.FrequencyQuantity, dependency.Days.ToArray(), dependency.IsBadHabit, dependency.NextDueDate))
            .IsSuccess.Should().BeTrue();
        (await service.IsCurrentAsync(_userId, execution, default)).Should().BeFalse();
    }

    private void ScriptParentBatch() => ScriptToolCall("""
        {"habits":[{"title":"Take vitamin D","description":"Keep with food","emoji":"💊","frequency_unit":"Week","frequency_quantity":1,"due_time":"13:00","end_date":"2027-12-31"},{"title":"Take rovustatina","frequency_unit":"Day","frequency_quantity":1,"due_time":"22:00","is_bad_habit":false}]}
        """, "bulk_create_habits");

    [Theory]
    [InlineData("en", OwnerParentRequest)]
    [InlineData("pt-BR", "Post lunch: tomar vitamina D (uma vez por semana) Before bed: tomar rovustatina (todo dia)")]
    public async Task ExistingParents_SeparateModelCallsBecomeOneBatchWithIndependentSchedules(string language, string message)
    {
        var parents = ParentAccount(language);
        var vitamin = language == "en" ? "Take vitamin D" : "Tomar vitamina D";
        var statin = language == "en" ? "Take rovustatina" : "Tomar rovustatina";
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ToolCalls =
            [
                new("create_habit", "vitamin", JsonSerializer.SerializeToElement(new { title = vitamin, frequency_unit = "Week", frequency_quantity = 1 })),
                new("create_habit", "statin", JsonSerializer.SerializeToElement(new { title = statin, frequency_unit = "Day", frequency_quantity = 1 }))
            ],
                ConversationContext = new()
            }));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new AiResponse { TextMessage = "Nothing saved." }));

        var held = await CreateHandler().Handle(new(_userId, message), default);

        var pending = held.Value.PendingOperations.Should().ContainSingle().Which;
        var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        stored.OperationId.Should().Be("bulk_create_habits");
        var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;
        var approved = await _executor.ExecuteAsync(new(_userId, stored.OperationId, stored.Arguments,
            stored.Surface, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));
        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        await _habitRepo.Received(1).AddAsync(Arg.Is<Habit>(habit => habit.Title == vitamin
            && habit.ParentHabitId == parents[0].Id && habit.FrequencyUnit == FrequencyUnit.Week), Arg.Any<CancellationToken>());
        await _habitRepo.Received(1).AddAsync(Arg.Is<Habit>(habit => habit.Title == statin
            && habit.ParentHabitId == parents[1].Id && habit.FrequencyUnit == FrequencyUnit.Day), Arg.Any<CancellationToken>());
    }

    private PendingOperationRevisionService ParentRevisionService() => new(_pendingOperationStore, _previewer,
        new RevisePendingOperationRequestValidator(), _toolRegistry);

    private Habit[] ParentAccount(string language)
    {
        _user.SetLanguage(language);
        var parents = new[] { "Post Lunch", "Before Bed" }.Select(title => Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            _userId, title, FrequencyUnit.Day, 1, Today)).Value).ToArray();
        UseParentHabits(parents);
        var config = Substitute.For<Orbit.Domain.Interfaces.IAppConfigService>();
        config.GetAsync(AppConfigKeys.MaxHabitDepth, AppConstants.MaxHabitDepth, Arg.Any<CancellationToken>()).Returns(AppConstants.MaxHabitDepth);
        var childHandler = new CreateSubHabitCommandHandler(_habitRepo, _tagRepo, _userDateService,
            _unitOfWork, config, _payGate, _flagCache);
        _mediator.Send(Arg.Any<CreateSubHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => childHandler.Handle(call.Arg<CreateSubHabitCommand>(), call.Arg<CancellationToken>()));
        _mediator.Send(Arg.Any<Orbit.Application.Chat.Tools.CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        return parents;
    }

    private void UseParentHabits(Habit[] habits)
    {
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        _habitRepo.FindOneTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.FirstOrDefault(call.Arg<Expression<Func<Habit, bool>>>().Compile()));
    }
}
