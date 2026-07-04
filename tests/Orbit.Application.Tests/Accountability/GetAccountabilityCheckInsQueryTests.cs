using FluentAssertions;
using NSubstitute;
using Orbit.Application.Accountability.Queries;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Application.Tests.Social;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Accountability;

public class GetAccountabilityCheckInsQueryTests
{
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<AccountabilityPair> _pairRepository = Substitute.For<IGenericRepository<AccountabilityPair>>();
    private readonly IGenericRepository<AccountabilityCheckIn> _checkInRepository = Substitute.For<IGenericRepository<AccountabilityCheckIn>>();

    private readonly GetAccountabilityCheckInsQueryHandler _handler;

    private readonly User _caller = SocialTestHelpers.OptedInUser("Caller");
    private readonly User _buddy = SocialTestHelpers.OptedInUser("Buddy");
    private readonly AccountabilityPair _pair;

    public GetAccountabilityCheckInsQueryTests()
    {
        var guard = new SocialAccessGuard(_userRepository);
        _handler = new GetAccountabilityCheckInsQueryHandler(guard, _pairRepository, _checkInRepository, _userRepository);

        _pair = AccountabilityPair.Create(_caller.Id, _buddy.Id, AccountabilityCadence.Daily).Value;
        _pair.Accept();

        SocialTestHelpers.StubUsers(_userRepository, _caller, _buddy);
        AccountabilityTestHelpers.StubFind(_pairRepository, _pair);
    }

    [Fact]
    public async Task Participant_ReturnsCheckInsNewestFirstWithAuthorDetails()
    {
        var olderDate = new DateOnly(2026, 6, 28);
        var newerDate = new DateOnly(2026, 6, 30);
        AccountabilityTestHelpers.StubFind(_checkInRepository,
            AccountabilityCheckIn.Create(_pair.Id, _buddy.Id, olderDate, "buddy note").Value,
            AccountabilityCheckIn.Create(_pair.Id, _caller.Id, newerDate, "my note").Value);

        var result = await _handler.Handle(new GetAccountabilityCheckInsQuery(_caller.Id, _pair.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(2);
        result.Value.Items[0].Date.Should().Be(newerDate);
        result.Value.Items[0].UserId.Should().Be(_caller.Id);
        result.Value.Items[0].DisplayName.Should().Be(_caller.Name);
        result.Value.Items[1].Date.Should().Be(olderDate);
        result.Value.Items[1].DisplayName.Should().Be(_buddy.Name);
    }

    [Fact]
    public async Task NonParticipant_ReturnsPairNotFound()
    {
        var stranger = SocialTestHelpers.OptedInUser("Stranger");
        SocialTestHelpers.StubUsers(_userRepository, _caller, _buddy, stranger);

        var result = await _handler.Handle(new GetAccountabilityCheckInsQuery(stranger.Id, _pair.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.PairNotFound);
    }
}
