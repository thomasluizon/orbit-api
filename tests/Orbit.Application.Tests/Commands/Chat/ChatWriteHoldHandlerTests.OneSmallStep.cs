using System.Text.Json;
using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    [Theory]
    [InlineData("en", "I cannot start studying", "Open the book", "Touch the book", false)]
    [InlineData("en", "The report feels impossible to begin", "Open the document", "Touch the keyboard", true)]
    [InlineData("pt-BR", "Não consigo começar a estudar", "Abrir o livro", "Encostar no livro", true)]
    [InlineData("pt-BR", "Travei antes de começar o relatório", "Abrir o documento", "Encostar no teclado", false)]
    public async Task OneSmallStep_ReplaysSmallerThenApprovedTaskAndReadsPersistedStep(
        string language, string message, string first, string smaller, bool composition)
    {
        _user.SetLanguage(language);
        if (composition) _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>()).Returns(new[] { FeatureFlagKeys.AstraBlockFirst });
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["oneSmallStep"], SupportsPendingOperationChanges: true);
        var firstResponse = await ReplayStartingStep(message, message, first, language, client);
        var card = StartingStepCard(firstResponse);
        card.Suggestion.Step.Should().Be(first);
        card.Actions.Select(action => action.Id).Should().Equal("smaller", "saveTask", "saveChecklistItem");
        firstResponse.Actions.Should().BeEmpty();
        firstResponse.PendingOperations.Should().BeNullOrEmpty();
        firstResponse.AiMessage.Should().BeNullOrEmpty();
        firstResponse.Parts!.OfType<ChatTextPart>().Should().BeEmpty();
        await _habitRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);

        var wire = JsonSerializer.Serialize(firstResponse, ChatStreamEvent.SerializerOptions);
        var storedResponse = JsonSerializer.Deserialize<ChatResponse>(wire, ChatStreamEvent.SerializerOptions)!;
        var replay = ChatResponseNegotiation.ForClient(storedResponse, client, []);
        ChatStreamEvent.Final(replay).ToJson().Should().Be(ChatStreamEvent.Final(firstResponse).ToJson());
        foreach (var unsupported in new AgentClientContext?[] { null, client with { PartKinds = [] } })
        {
            var legacy = ChatResponseNegotiation.ForClient(storedResponse, unsupported, [], language);
            legacy.AiMessage.Should().Be(first);
            legacy.Parts?.OfType<ChatBlockPart>().Should().BeEmpty();
        }
        var disabled = ChatResponseNegotiation.ForClient(storedResponse, client, [FeatureFlagKeys.AstraPartsDisabled], language);
        disabled.AiMessage.Should().Be(first);
        disabled.Parts.Should().BeNull();

        var refined = StartingStepCard(await ReplayStartingStep(card.Actions[0].Message, message, smaller, language, client));
        refined.Suggestion.Goal.Should().Be(message);
        refined.Suggestion.Step.Should().Be(smaller).And.NotBe(first);
        var repository = new GenericRepository<Habit>(_dbContext);
        _habitRepo.AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>()).Returns(async call =>
            await repository.AddAsync(call.Arg<Habit>(), call.Arg<CancellationToken>()));
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(call => _dbContext.SaveChangesAsync(call.Arg<CancellationToken>()));
        ScriptToolCall(JsonSerializer.Serialize(new { title = smaller, frequency_unit = "OneTime", due_date = Today.ToString("yyyy-MM-dd") }));
        var preview = await CreateHandler().Handle(new(_userId, refined.Actions[1].Message, ClientContext: client), default);
        preview.IsSuccess.Should().BeTrue(preview.Error);
        var pending = preview.Value.PendingOperations.Should().ContainSingle().Which;
        pending.Items.Should().ContainSingle().Which.EntityName.Should().Be(smaller);
        (await _dbContext.Habits.CountAsync()).Should().Be(0);
        var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;
        var approved = await _executor.ExecuteAsync(new(_userId, stored.OperationId, stored.Arguments,
            stored.Surface, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));
        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        _dbContext.ChangeTracker.Clear();
        var saved = await _dbContext.Habits.AsNoTracking().SingleAsync();
        var query = new GetHabitByIdQueryHandler(repository, new GenericRepository<HabitLog>(_dbContext), _userDateService);
        var readback = await query.Handle(new(_userId, saved.Id), default);
        readback.IsSuccess.Should().BeTrue(readback.Error);
        readback.Value.Title.Should().Be(smaller);
        readback.Value.Schedule.Should().Be(HabitSchedule.OneTime);
    }

    public static IEnumerable<object[]> StartingStepClientCases()
    {
        foreach (var language in new[] { "en", "pt-BR" })
            foreach (var clientMode in new[] { "legacy", "unknownKind", "partsDisabled", "supported" })
                foreach (var streaming in new[] { false, true })
                    foreach (var composition in new[] { false, true })
                        yield return [language, clientMode, streaming, composition];
    }

    [Theory]
    [MemberData(nameof(StartingStepClientCases))]
    public async Task OneSmallStep_FreshReplyNegotiatesClientCapabilities(
        string language, string clientMode, bool streaming, bool composition)
    {
        _user.SetLanguage(language);
        var flags = new List<string>();
        if (composition) flags.Add(FeatureFlagKeys.AstraBlockFirst);
        if (clientMode == "partsDisabled") flags.Add(FeatureFlagKeys.AstraPartsDisabled);
        _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>()).Returns(flags);
        AgentClientContext? client = clientMode == "legacy" ? null
            : new(SupportsParts: true, PartKinds: clientMode == "unknownKind" ? [] : ["oneSmallStep"]);
        var goal = language == "en" ? "Study" : "Estudar";
        var step = language == "en" ? "Open the book" : "Abrir o livro";
        var response = await ReplayStartingStep(goal, goal, step, language, client, streaming);
        var wire = streaming ? ChatStreamEvent.Final(response).ToJson()
            : JsonSerializer.Serialize(response, ChatStreamEvent.SerializerOptions);
        using var document = JsonDocument.Parse(wire);
        var delivered = streaming ? document.RootElement.GetProperty("response") : document.RootElement;

        if (clientMode == "supported")
        {
            StartingStepCard(response).Suggestion.Step.Should().Be(step);
            response.AiMessage.Should().BeNull();
            delivered.GetProperty("parts")[0].GetProperty("kind").GetString().Should().Be("oneSmallStep");
            delivered.GetProperty("parts")[0].GetProperty("block").GetProperty("suggestion")
                .GetProperty("step").GetString().Should().Be(step);
        }
        else
        {
            response.AiMessage.Should().Be(step);
            delivered.GetProperty("aiMessage").GetString().Should().Be(step);
            (response.Parts ?? []).OfType<ChatBlockPart>().Should().BeEmpty();
            if (clientMode == "unknownKind")
            {
                response.Parts.Should().ContainSingle().Which.Should().Be(new ChatTextPart(step));
                delivered.GetProperty("parts")[0].GetProperty("type").GetString().Should().Be("text");
                delivered.GetProperty("parts")[0].GetProperty("text").GetString().Should().Be(step);
            }
            else
            {
                response.Parts.Should().BeNull();
                delivered.TryGetProperty("parts", out _).Should().BeFalse();
            }
        }
    }

    [Theory]
    [InlineData("en", "Open the book")]
    [InlineData("pt-BR", "Abrir o livro")]
    public async Task OneSmallStep_ChecklistSavePreservesExistingItemsAndWaitsForApproval(string language, string step)
    {
        _user.SetLanguage(language);
        var parent = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, "Study", FrequencyUnit.Day, 1, Today,
            ChecklistItems: [new ChecklistItem("Existing item", true)])).Value;
        _dbContext.Habits.Add(parent);
        await _dbContext.SaveChangesAsync();
        var repository = new GenericRepository<Habit>(_dbContext);
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => repository.FindAsync(call.Arg<Expression<Func<Habit, bool>>>(), call.Arg<CancellationToken>()));
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => repository.FindAsync(call.Arg<Expression<Func<Habit, bool>>>(),
                call.ArgAt<Func<IQueryable<Habit>, IQueryable<Habit>>?>(1), call.Arg<CancellationToken>()));
        _habitRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => repository.GetByIdAsync(call.Arg<Guid>(), call.Arg<CancellationToken>()));
        _habitRepo.FindOneTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => repository.FindOneTrackedAsync(call.Arg<Expression<Func<Habit, bool>>>(),
                call.ArgAt<Func<IQueryable<Habit>, IQueryable<Habit>>?>(1), call.Arg<CancellationToken>()));
        _mediator.Send(Arg.Any<Orbit.Application.Chat.Tools.CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var update = new UpdateChecklistCommandHandler(repository, _unitOfWork);
        _mediator.Send(Arg.Any<UpdateChecklistCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
            update.Handle(call.Arg<UpdateChecklistCommand>(), call.Arg<CancellationToken>()));
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(call => _dbContext.SaveChangesAsync(call.Arg<CancellationToken>()));
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["oneSmallStep"], SupportsPendingOperationChanges: true);
        var card = StartingStepCard(await ReplayStartingStep("Help me begin", "Study", step, language, client));
        ScriptToolCall(JsonSerializer.Serialize(new
        {
            habit_id = parent.Id,
            checklist_items = new[]
        {
            new { text = "Existing item", is_checked = true }, new { text = step, is_checked = false }
        }
        }), "update_checklist");
        var preview = await CreateHandler().Handle(new(_userId, card.Actions[2].Message, ClientContext: client), default);
        preview.IsSuccess.Should().BeTrue(preview.Error);
        var pending = preview.Value.PendingOperations.Should().ContainSingle().Which;
        parent.ChecklistItems.Should().ContainSingle();
        var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        stored.Arguments.GetProperty("checklist_items").GetArrayLength().Should().Be(2);
        var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;
        var approved = await _executor.ExecuteAsync(new(_userId, stored.OperationId, stored.Arguments,
            stored.Surface, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));
        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        _dbContext.ChangeTracker.Clear();
        var read = await new GetHabitByIdQueryHandler(repository, new GenericRepository<HabitLog>(_dbContext), _userDateService)
            .Handle(new(_userId, parent.Id), default);
        read.Value.ChecklistItems.Select(item => item.Text).Should().Equal("Existing item", step);
        read.Value.ChecklistItems[0].IsChecked.Should().BeTrue();
    }

    private async Task<ChatResponse> ReplayStartingStep(string message, string goal, string step, string language,
        AgentClientContext? client, bool streaming = true)
    {
        ScriptToolCall(JsonSerializer.Serialize(new
        {
            title = goal,
            one_small_step = true,
            language,
            suggested_sub_habits = new[] { new { title = step } }
        }), "suggest_breakdown");
        var events = new List<ChatStreamEvent>();
        Func<ChatStreamEvent, Task>? sink = streaming ? item => { events.Add(item); return Task.CompletedTask; } : null;
        var result = await CreateHandler().Handle(new(_userId, message, ClientContext: client, StreamSink: sink), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        events.Should().NotContain(item => item.Type == "delta");
        return result.Value;
    }

    private static OneSmallStepCard StartingStepCard(ChatResponse response) => response.Parts!.OfType<ChatBlockPart>()
        .Should().ContainSingle().Which.Block.Should().BeOfType<OneSmallStepCard>().Subject;
}
