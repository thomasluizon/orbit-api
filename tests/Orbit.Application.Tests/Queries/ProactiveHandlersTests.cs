using FluentAssertions;
using NSubstitute;
using Orbit.Application.Proactive.Commands;
using Orbit.Application.Proactive.Queries;
using Orbit.Application.Proactive.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Queries;

public sealed class ProactiveHandlersTests
{
    [Fact]
    public async Task Current_MapsItemAndEmptyState()
    {
        var engine = Substitute.For<IProactiveEngine>();
        var userId = Guid.NewGuid();
        var handler = new GetCurrentProactiveQueryHandler(engine);
        (await handler.Handle(new(userId), default)).Value.Should().BeNull();
        var item = ProactiveItem.Create(userId, ProactiveTrigger.EveningCheckin, new(2030, 10, 14),
            "{\"Trigger\":2,\"HabitTitles\":[\"Read\"]}", "Read is open.", "See habits", "Show my habits.",
            new(2030, 10, 14, 19, 0, 0, DateTimeKind.Utc));
        engine.GetCurrentAsync(userId, default).Returns(item);
        var result = await handler.Handle(new(userId), default);
        result.Value!.Id.Should().Be(item.Id);
        result.Value.Trigger.Should().Be("EveningCheckin");
        result.Value.Facts!.HabitTitles.Should().Equal("Read");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Outcome_ForwardsOwnerItemAndKind(bool acted)
    {
        var engine = Substitute.For<IProactiveEngine>();
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        engine.RecordOutcomeAsync(userId, itemId, acted, default).Returns(Result.Success());
        var result = await new RecordProactiveOutcomeCommandHandler(engine).Handle(new(userId, itemId, acted), default);
        result.IsSuccess.Should().BeTrue();
        await engine.Received(1).RecordOutcomeAsync(userId, itemId, acted, default);
    }
}
