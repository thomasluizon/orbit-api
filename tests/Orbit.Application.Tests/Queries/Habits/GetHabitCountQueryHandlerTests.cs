using FluentAssertions;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Habits;

public class GetHabitCountQueryHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepository = Substitute.For<IGenericRepository<Habit>>();
    private readonly GetHabitCountQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetHabitCountQueryHandlerTests()
    {
        _handler = new GetHabitCountQueryHandler(_habitRepository);
    }

    [Fact]
    public async Task Handle_ReturnsNonGeneralHabitCount()
    {
        _habitRepository.CountAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(7);

        var result = await _handler.Handle(new GetHabitCountQuery(UserId), CancellationToken.None);

        result.Count.Should().Be(7);
        await _habitRepository.Received(1).CountAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>());
    }
}
