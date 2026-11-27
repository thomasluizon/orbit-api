using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public partial class AiBlockAnswerServiceTests
{
    [Theory]
    [InlineData("pt-BR", "Pular")]
    [InlineData("en", "Skip")]
    public async Task PickListSkipAnswer_UsesTheClientLocaleInTheFirstResponseTheStoredBlockAndTheReplay(string locale, string skip)
    {
        var habits = new[] { "Água", "Ler" }.Select(title => Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId,
            title, FrequencyUnit.Day, 1, new DateOnly(2026, 4, 3))).Value).ToArray();
        var repository = Substitute.For<IGenericRepository<Habit>>();
        repository.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
            Arg.Any<CancellationToken>()).Returns(habits);
        _dates.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 3));
        var previewer = new PendingOperationChangePreviewer(repository, Substitute.For<IGenericRepository<Goal>>(),
            Substitute.For<IGenericRepository<Tag>>(), _dates, new AiToolRegistry([]));
        var items = habits.Select(habit => new AiBlockItem(habit.Id.ToString(), habit.Title,
            JsonSerializer.SerializeToElement(new { habit_id = habit.Id, date = "2026-04-03" }))).ToArray();
        var block = IssuedAiBlock.Create(UserId, new("pickList", "Choose", [], items, true, Action: "skip_habits"));
        _store.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        var pendingId = Guid.NewGuid();
        AgentExecuteOperationRequest? captured = null;
        _executor.ExecuteAsync(Arg.Do<AgentExecuteOperationRequest>(value => captured = value), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var request = call.Arg<AgentExecuteOperationRequest>();
                var preview = (await previewer.PreviewAsync(UserId, request.OperationId, request.Arguments, default,
                    request.ClientContext)).Value!;
                return new AgentExecuteOperationResponse(new(request.OperationId, request.OperationId, AgentRiskClass.Low,
                    AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.PendingConfirmation),
                    new PendingAgentOperation(pendingId, "habits", "Habits", "Preview", AgentRiskClass.Low,
                        AgentConfirmationRequirement.FreshConfirmation, block.ExpiresAtUtc, preview.Changes,
                        preview.ChangeTargetCount, preview.Items, preview.PreviewFingerprint));
            });
        string? storedResponse = null;
        _store.RecordAnswerAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<string>(), Arg.Any<DateOnly?>(),
            Arg.Do<string?>(value => storedResponse = value), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _featureFlags.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>()).Returns([]);
        var pendingOperations = Substitute.For<IPendingAgentOperationStore>();
        var service = new AiBlockAnswerService(_store, _work, _quota, _executor, _users, _analytics, _featureFlags,
            pendingOperations, _dates, NullLogger.Instance, previewer, clarifications: _clarifications);
        var conversation = new PendingOperationConversation(Guid.NewGuid(), new DateTime(2026, 4, 3, 9, 0, 0, DateTimeKind.Utc));
        var answer = Request(block.Id, items.Select(item => item.Id).ToArray()) with
        {
            ClientContext = new(Locale: locale, SupportsPendingOperationChanges: true, ConversationId: conversation.Id),
            PendingOperationConversation = conversation
        };

        var first = await service.PrepareAsync(answer, default);

        first.IsSuccess.Should().BeTrue(first.Error);
        SkipValues(first.Value.Response!).Should().HaveCount(2).And.OnlyContain(value => value == skip);
        captured!.ClientContext.Should().Be(answer.ClientContext);
        captured.PendingOperationConversation.Should().Be(conversation);
        var stored = JsonSerializer.Deserialize<ChatResponse>(storedResponse!, ChatStreamEvent.SerializerOptions)!;
        SkipValues(stored).Should().HaveCount(2).And.OnlyContain(value => value == skip);

        block.MarkAnswered(block.CreatedAtUtc);
        typeof(IssuedAiBlock).GetProperty(nameof(block.AnswerJson))!.SetValue(block, IssuedAiBlock.SerializeAnswer(answer.BlockAnswer!));
        typeof(IssuedAiBlock).GetProperty(nameof(block.ResponseJson))!.SetValue(block, storedResponse);
        pendingOperations.IsAwaitingConfirmation(UserId, pendingId).Returns(true);
        pendingOperations.GetExecution(UserId, pendingId).Returns(new PendingAgentOperationExecution(pendingId, "habits",
            captured.OperationId, captured.Arguments, AgentExecutionSurface.Chat, AgentConfirmationRequirement.FreshConfirmation,
            first.Value.Response!.PendingOperations![0].PreviewFingerprint));

        var replay = await service.PrepareAsync(answer, default);

        replay.IsSuccess.Should().BeTrue(replay.Error);
        SkipValues(replay.Value.Response!).Should().HaveCount(2).And.OnlyContain(value => value == skip);
    }

    private static IEnumerable<string?> SkipValues(ChatResponse response) => response.PendingOperations!.Single().Items!
        .SelectMany(item => item.Fields).Where(field => field.Field == "action").Select(field => field.NewValue);
}
