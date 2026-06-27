using FluentAssertions;
using NSubstitute;
using Orbit.Application.Social.Queries;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Social;

public class GetCheersQueryTests
{
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Cheer> _cheerRepository = Substitute.For<IGenericRepository<Cheer>>();
    private readonly GetCheersQueryHandler _handler;

    private readonly User _caller = SocialTestHelpers.OptedInUser("Caller");
    private readonly User _friend = SocialTestHelpers.OptedInUser("Friend");

    public GetCheersQueryTests()
    {
        var guard = new SocialAccessGuard(_userRepository);
        _handler = new GetCheersQueryHandler(guard, _cheerRepository, _userRepository);
        SocialTestHelpers.StubUsers(_userRepository, _caller, _friend);
    }

    [Fact]
    public async Task Received_ReturnsCheersWithSenderDisplayFields()
    {
        var received = Cheer.Create(_friend.Id, _caller.Id, Guid.NewGuid(), "proud of you").Value;
        var sent = Cheer.Create(_caller.Id, _friend.Id, Guid.NewGuid(), "go go").Value;
        SocialTestHelpers.StubFind(_cheerRepository, received, sent);

        var result = await _handler.Handle(new GetCheersQuery(_caller.Id, "received"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle();
        var item = result.Value.Items[0];
        item.SenderId.Should().Be(_friend.Id);
        item.SenderDisplayName.Should().Be("Friend");
        item.SenderHandle.Should().Be(_friend.Handle);
        item.Note.Should().Be("proud of you");
    }

    [Fact]
    public async Task Sent_ReturnsOnlyCheersTheCallerSent()
    {
        var received = Cheer.Create(_friend.Id, _caller.Id, Guid.NewGuid(), "a").Value;
        var sent = Cheer.Create(_caller.Id, _friend.Id, Guid.NewGuid(), "b").Value;
        SocialTestHelpers.StubFind(_cheerRepository, received, sent);

        var result = await _handler.Handle(new GetCheersQuery(_caller.Id, "sent"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle(c => c.Id == sent.Id);
    }

    [Fact]
    public async Task CallerOptedOut_ReturnsSocialDisabled()
    {
        var optedOut = SocialTestHelpers.OptedOutUser();
        SocialTestHelpers.StubUsers(_userRepository, optedOut);

        var result = await _handler.Handle(new GetCheersQuery(optedOut.Id, "received"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }
}
