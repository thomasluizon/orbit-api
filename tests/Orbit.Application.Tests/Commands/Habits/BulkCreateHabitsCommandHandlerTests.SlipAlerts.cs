using FluentAssertions;
using NSubstitute;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Commands.Habits;

public partial class BulkCreateHabitsCommandHandlerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Handle_DeniedSlipAlertAtAnyDepth_TracksNoFamilyOrTags(int depth)
    {
        _payGate.CanUseSlipAlerts(UserId, Arg.Any<CancellationToken>()).Returns(Result.PayGateFailure("Pro required"));
        var item = SlipAlertFamily(depth);

        var result = await _handler.Handle(new(UserId, [new("Safe", null, FrequencyUnit.Day, 1), item]), default);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
        await _payGate.Received().CanCreateHabits(UserId, 2, Arg.Any<CancellationToken>());
        await _payGate.Received(1).CanUseSlipAlerts(UserId, Arg.Any<CancellationToken>());
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        await _tagRepo.DidNotReceive().AddAsync(Arg.Any<Tag>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Handle_AllowedSlipAlertAtAnyDepth_PersistsEntireFamily(int depth)
    {
        _payGate.CanUseSlipAlerts(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());

        var result = await _handler.Handle(new(UserId, [SlipAlertFamily(depth)]), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AppliedCount.Should().Be(1);
        await _habitRepo.Received(depth + 1).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        await _habitRepo.Received(1).AddAsync(Arg.Is<Habit>(habit => habit.SlipAlertEnabled), Arg.Any<CancellationToken>());
        await _payGate.Received(1).CanUseSlipAlerts(UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DisabledSlipAlert_DoesNotRequireEntitlement()
    {
        _payGate.CanUseSlipAlerts(UserId, Arg.Any<CancellationToken>()).Returns(Result.PayGateFailure("Pro required"));

        var result = await _handler.Handle(new(UserId, [new("Smoking", null, FrequencyUnit.Day, 1, IsBadHabit: true)]), default);

        result.Value.AppliedCount.Should().Be(1);
        await _payGate.DidNotReceive().CanUseSlipAlerts(UserId, Arg.Any<CancellationToken>());
    }

    private static BulkHabitItem SlipAlertFamily(int depth)
    {
        var item = new BulkHabitItem("Smoking", null, FrequencyUnit.Day, 1,
            IsBadHabit: true, SlipAlertEnabled: true, Tags: ["Health"]);
        for (var level = 0; level < depth; level++)
            item = new($"Routine {level}", null, FrequencyUnit.Day, 1, SubHabits: [item]);
        return item;
    }
}
