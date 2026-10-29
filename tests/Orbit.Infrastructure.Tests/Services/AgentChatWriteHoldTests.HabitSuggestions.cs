using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Tests.Services;

public partial class AgentChatWriteHoldTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HabitSuggestion_RealPreviewNeverDispatchesTheWrite(bool existing)
    {
        var today = new DateOnly(2026, 10, 26);
        var draft = new HabitSuggestionDraft(Guid.NewGuid(), "📖", "Read", "Habit", FrequencyUnit.Day, 1,
            [], today, new(22, 0), new(22, 15), null, [], "A quiet pause before bed.");
        if (existing) _dbContext.Habits.Add(draft.ToHabit(_userId).Value);
        await _dbContext.SaveChangesAsync();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(today);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Orbit.Domain.Common.Result.Success());
        var habits = new GenericRepository<Habit>(_dbContext);
        var goals = new GenericRepository<Goal>(_dbContext);
        var registry = new AiToolRegistry([new CreateHabitTool(dates, mediator), new UpdateHabitTool(habits, dates, mediator)]);
        var previewer = new PendingOperationChangePreviewer(habits, goals, new GenericRepository<Tag>(_dbContext), dates, registry);
        var handler = new PreviewHabitSuggestionCommandHandler(
            new(habits, goals, dates, Substitute.For<IHabitDraftGenerator>()), CreateExecutor(registry, previewer));

        var result = await handler.Handle(new(_userId, draft), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        result.Value.Operation.OperationId.Should().Be(existing ? "update_habit" : "create_habit");
        result.Value.PendingOperation!.PreviewFingerprint.Should().NotBeNullOrEmpty();
        result.Value.PendingOperation.Items.Should().ContainSingle();
        _dbContext.Habits.Count().Should().Be(existing ? 1 : 0);
        mediator.ReceivedCalls().Should().OnlyContain(call => call.GetArguments()[0] is CheckChatCommandQuery);
    }
}
