using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Accountability.Commands;
using Orbit.Application.Accountability.Services;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Application.Tests.Social;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Accountability;

public class SetAccountabilityHabitsCommandTests
{
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<AccountabilityPair> _pairRepository = Substitute.For<IGenericRepository<AccountabilityPair>>();
    private readonly IGenericRepository<AccountabilityPairHabit> _pairHabitRepository = Substitute.For<IGenericRepository<AccountabilityPairHabit>>();
    private readonly IGenericRepository<Habit> _habitRepository = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private readonly SetAccountabilityHabitsCommandHandler _handler;

    private readonly User _caller = SocialTestHelpers.OptedInUser("Caller");
    private readonly User _buddy = SocialTestHelpers.OptedInUser("Buddy");
    private readonly AccountabilityPair _pair;
    private readonly Guid _newHabitId = Guid.NewGuid();

    public SetAccountabilityHabitsCommandTests()
    {
        var guard = new SocialAccessGuard(_userRepository);
        var pairService = new AccountabilityPairService(_pairRepository, _pairHabitRepository, _habitRepository);
        _handler = new SetAccountabilityHabitsCommandHandler(guard, pairService, _unitOfWork);

        _pair = AccountabilityPair.Create(_caller.Id, _buddy.Id, AccountabilityCadence.Daily).Value;
        _pair.Accept();

        SocialTestHelpers.StubUsers(_userRepository, _caller, _buddy);
        AccountabilityTestHelpers.StubFind(_pairRepository, _pair);
        AccountabilityTestHelpers.StubFind(_pairHabitRepository,
            AccountabilityPairHabit.Create(_pair.Id, _caller.Id, Guid.NewGuid()));
        _habitRepository.CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns(1);
    }

    private SetAccountabilityHabitsCommand Command() =>
        new(_caller.Id, _pair.Id, new[] { _newHabitId });

    [Fact]
    public async Task ReplacesCallersLinkedHabits()
    {
        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _pairHabitRepository.Received(1).RemoveRange(Arg.Is<IEnumerable<AccountabilityPairHabit>>(rows =>
            rows.Any(r => r.UserId == _caller.Id)));
        await _pairHabitRepository.Received(1).AddAsync(
            Arg.Is<AccountabilityPairHabit>(ph => ph.UserId == _caller.Id && ph.HabitId == _newHabitId),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HabitNotOwnedByCaller_ReturnsHabitNotFound()
    {
        _habitRepository.CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns(0);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.HabitNotFound);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonParticipant_ReturnsPairNotFound()
    {
        var stranger = SocialTestHelpers.OptedInUser("Stranger");
        SocialTestHelpers.StubUsers(_userRepository, _caller, _buddy, stranger);

        var result = await _handler.Handle(
            new SetAccountabilityHabitsCommand(stranger.Id, _pair.Id, new[] { _newHabitId }), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.PairNotFound);
    }

    [Fact]
    public async Task EndedPair_ReturnsPairNotFound()
    {
        _pair.End();

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.PairNotFound);
    }
}
