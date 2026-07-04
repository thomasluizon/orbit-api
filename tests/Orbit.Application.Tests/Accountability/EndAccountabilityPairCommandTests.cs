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

public class EndAccountabilityPairCommandTests
{
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<AccountabilityPair> _pairRepository = Substitute.For<IGenericRepository<AccountabilityPair>>();
    private readonly IGenericRepository<AccountabilityPairHabit> _pairHabitRepository = Substitute.For<IGenericRepository<AccountabilityPairHabit>>();
    private readonly IGenericRepository<Habit> _habitRepository = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private readonly EndAccountabilityPairCommandHandler _handler;

    private readonly User _requester = SocialTestHelpers.OptedInUser("Requester");
    private readonly User _addressee = SocialTestHelpers.OptedInUser("Addressee");
    private readonly AccountabilityPair _pair;

    public EndAccountabilityPairCommandTests()
    {
        var guard = new SocialAccessGuard(_userRepository);
        var pairService = new AccountabilityPairService(_pairRepository, _pairHabitRepository, _habitRepository);
        _handler = new EndAccountabilityPairCommandHandler(guard, pairService, _unitOfWork);

        _pair = AccountabilityPair.Create(_requester.Id, _addressee.Id, AccountabilityCadence.Daily).Value;
        _pair.Accept();

        SocialTestHelpers.StubUsers(_userRepository, _requester, _addressee);
        AccountabilityTestHelpers.StubFind(_pairRepository, _pair);
    }

    [Fact]
    public async Task ByRequester_EndsPair()
    {
        var result = await _handler.Handle(new EndAccountabilityPairCommand(_requester.Id, _pair.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _pair.Status.Should().Be(AccountabilityPairStatus.Ended);
        _pair.EndedAtUtc.Should().NotBeNull();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ByAddressee_EndsPair()
    {
        var result = await _handler.Handle(new EndAccountabilityPairCommand(_addressee.Id, _pair.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _pair.Status.Should().Be(AccountabilityPairStatus.Ended);
    }

    [Fact]
    public async Task ByNonParticipant_ReturnsPairNotFound()
    {
        var stranger = SocialTestHelpers.OptedInUser("Stranger");
        SocialTestHelpers.StubUsers(_userRepository, _requester, _addressee, stranger);

        var result = await _handler.Handle(new EndAccountabilityPairCommand(stranger.Id, _pair.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.PairNotFound);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AlreadyEnded_ReturnsPairNotFound()
    {
        _pair.End();

        var result = await _handler.Handle(new EndAccountabilityPairCommand(_requester.Id, _pair.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.PairNotFound);
    }
}
