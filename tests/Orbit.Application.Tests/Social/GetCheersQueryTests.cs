using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Social.Queries;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Social;

public class GetCheersQueryTests
{
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly ISocialGraphReader _reader = Substitute.For<ISocialGraphReader>();
    private readonly TimeProvider _timeProvider = Substitute.For<TimeProvider>();
    private readonly GetCheersQueryHandler _handler;

    private readonly User _caller = SocialTestHelpers.OptedInUser("Caller");
    private readonly User _friend = SocialTestHelpers.OptedInUser("Friend");
    private readonly DateTime _now = new(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc);

    public GetCheersQueryTests()
    {
        var guard = new SocialAccessGuard(_userRepository);
        _handler = new GetCheersQueryHandler(guard, _reader, _userRepository, _timeProvider);
        SocialTestHelpers.StubUsers(_userRepository, _caller, _friend);
        _timeProvider.GetUtcNow().Returns(new DateTimeOffset(_now));
    }

    private void StubReader(bool isReceived, params Cheer[] cheers) =>
        _reader.ReadCheersPageAsync(Arg.Any<Guid>(), isReceived, Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<Cheer>)cheers.ToList());

    [Fact]
    public async Task Received_MapsSenderDisplayFieldsFromReaderPage()
    {
        var received = Cheer.Create(_friend.Id, _caller.Id, Guid.NewGuid(), "proud of you").Value;
        StubReader(isReceived: true, received);

        var result = await _handler.Handle(new GetCheersQuery(_caller.Id, "received"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var item = result.Value.Items.Should().ContainSingle().Subject;
        item.SenderId.Should().Be(_friend.Id);
        item.SenderDisplayName.Should().Be("Friend");
        item.SenderHandle.Should().Be(_friend.Handle);
        item.Note.Should().Be("proud of you");
    }

    [Fact]
    public async Task Sent_QueriesReaderWithSentDirection()
    {
        var sent = Cheer.Create(_caller.Id, _friend.Id, Guid.NewGuid(), "go go").Value;
        StubReader(isReceived: false, sent);

        var result = await _handler.Handle(new GetCheersQuery(_caller.Id, "sent"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle(c => c.Id == sent.Id);
        await _reader.Received(1).ReadCheersPageAsync(
            _caller.Id, false, Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_QueriesReaderWithLookbackWindowAndPageCap()
    {
        StubReader(isReceived: true);

        await _handler.Handle(new GetCheersQuery(_caller.Id, "received"), CancellationToken.None);

        await _reader.Received(1).ReadCheersPageAsync(
            _caller.Id,
            true,
            _now.AddDays(-AppConstants.CheersLookbackDays),
            AppConstants.MaxCheersReturned,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UnknownSender_MapsEmptyDisplayFields()
    {
        var stranger = Guid.NewGuid();
        var received = Cheer.Create(stranger, _caller.Id, null, null).Value;
        StubReader(isReceived: true, received);

        var result = await _handler.Handle(new GetCheersQuery(_caller.Id, "received"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var item = result.Value.Items.Should().ContainSingle().Subject;
        item.SenderId.Should().Be(stranger);
        item.SenderHandle.Should().BeEmpty();
        item.SenderDisplayName.Should().BeEmpty();
    }

    [Fact]
    public async Task CallerOptedOut_ReturnsSocialDisabled()
    {
        var optedOut = SocialTestHelpers.OptedOutUser();
        SocialTestHelpers.StubUsers(_userRepository, optedOut);

        var result = await _handler.Handle(new GetCheersQuery(optedOut.Id, "received"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        await _reader.DidNotReceive().ReadCheersPageAsync(
            Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
