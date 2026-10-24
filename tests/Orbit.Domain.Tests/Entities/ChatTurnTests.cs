using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public sealed class ChatTurnTests
{
    [Fact]
    public void Complete_PreservesIdentityAndCannotReplaceOutcome()
    {
        var userId = Guid.NewGuid();
        var turn = ChatTurn.Create(userId, new string('A', 64), new string('B', 64), DateTime.UtcNow.AddMinutes(4));
        turn.Complete("terminal event");

        turn.UserId.Should().Be(userId);
        turn.TerminalEvent.Should().Be("terminal event");
        var replace = () => turn.Complete("another outcome");
        replace.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("not-a-hash", "not-a-hash")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG", "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")]
    public void Create_RejectsInvalidDigests(string key, string fingerprint)
    {
        var create = () => ChatTurn.Create(Guid.NewGuid(), key, fingerprint, DateTime.UtcNow);
        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_RejectsMissingUserAndNonUtcExpiry()
    {
        var missingUser = () => ChatTurn.Create(Guid.Empty, new string('A', 64), new string('B', 64), DateTime.UtcNow);
        missingUser.Should().Throw<ArgumentException>();
        var localExpiry = () => ChatTurn.Create(Guid.NewGuid(), new string('A', 64), new string('B', 64), DateTime.MinValue);
        localExpiry.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Complete_RejectsEmptyOutcome()
    {
        var turn = ChatTurn.Create(Guid.NewGuid(), new string('A', 64), new string('B', 64), DateTime.UtcNow);
        var complete = () => turn.Complete(" ");
        complete.Should().Throw<ArgumentException>();
    }
}
