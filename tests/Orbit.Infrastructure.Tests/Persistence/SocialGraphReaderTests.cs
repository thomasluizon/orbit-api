using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class SocialGraphReaderTests
{
    private static readonly DateTime Now = new(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc);

    private static T WithCreatedAt<T>(T entity, DateTime createdAtUtc)
    {
        typeof(T).GetProperty(nameof(Cheer.CreatedAtUtc))!.SetValue(entity, createdAtUtc);
        return entity;
    }

    private static Friendship Accepted(Guid requester, Guid addressee)
    {
        var friendship = Friendship.Create(requester, addressee).Value;
        friendship.Accept();
        return friendship;
    }

    [Fact]
    public void VisibleFriendships_ExcludeBlockedCounterparty_InEitherDirection()
    {
        var caller = Guid.NewGuid();
        var friend = Guid.NewGuid();
        var iBlocked = Guid.NewGuid();
        var blockedMe = Guid.NewGuid();

        var friendships = new[]
        {
            Accepted(caller, friend),
            Accepted(caller, iBlocked),
            Accepted(blockedMe, caller)
        }.AsQueryable();
        var blocks = new[]
        {
            BlockedUser.Create(caller, iBlocked).Value,
            BlockedUser.Create(blockedMe, caller).Value
        }.AsQueryable();

        var result = SocialGraphReader.BuildVisibleFriendships(friendships, blocks, caller, 100).ToList();

        result.Should().HaveCount(1);
        var kept = result.Single();
        (kept.RequesterId == friend || kept.AddresseeId == friend).Should().BeTrue();
    }

    [Fact]
    public void VisibleFriendships_IgnoreRowsNotInvolvingTheUser()
    {
        var caller = Guid.NewGuid();
        var mine = Accepted(caller, Guid.NewGuid());
        var strangers = Accepted(Guid.NewGuid(), Guid.NewGuid());

        var result = SocialGraphReader
            .BuildVisibleFriendships(new[] { mine, strangers }.AsQueryable(), Array.Empty<BlockedUser>().AsQueryable(), caller, 100)
            .ToList();

        result.Should().ContainSingle().Which.Should().Be(mine);
    }

    [Fact]
    public void VisibleFriendships_OrderAcceptedFirstThenNewest()
    {
        var caller = Guid.NewGuid();
        var oldAccepted = WithCreatedAt(Accepted(caller, Guid.NewGuid()), Now.AddDays(-10));
        var newAccepted = WithCreatedAt(Accepted(caller, Guid.NewGuid()), Now.AddDays(-1));
        var pending = WithCreatedAt(Friendship.Create(caller, Guid.NewGuid()).Value, Now);

        var result = SocialGraphReader
            .BuildVisibleFriendships(new[] { pending, oldAccepted, newAccepted }.AsQueryable(), Array.Empty<BlockedUser>().AsQueryable(), caller, 100)
            .ToList();

        result.Should().ContainInOrder(newAccepted, oldAccepted, pending);
    }

    [Fact]
    public void VisibleFriendships_CapAtLimit()
    {
        var caller = Guid.NewGuid();
        var rows = Enumerable.Range(0, 5)
            .Select(i => WithCreatedAt(Accepted(caller, Guid.NewGuid()), Now.AddDays(-i)))
            .ToArray();

        var result = SocialGraphReader
            .BuildVisibleFriendships(rows.AsQueryable(), Array.Empty<BlockedUser>().AsQueryable(), caller, 3)
            .ToList();

        result.Should().HaveCount(3);
        result.Should().ContainInOrder(rows[0], rows[1], rows[2]);
    }

    [Fact]
    public void VisibleCheers_Received_ExcludeBlockedSenders()
    {
        var caller = Guid.NewGuid();
        var friend = Guid.NewGuid();
        var iBlocked = Guid.NewGuid();
        var blockedMe = Guid.NewGuid();

        var cheers = new[]
        {
            Cheer.Create(friend, caller, null, "hi").Value,
            Cheer.Create(iBlocked, caller, null, "x").Value,
            Cheer.Create(blockedMe, caller, null, "y").Value,
            Cheer.Create(caller, friend, null, "outbound").Value
        }.AsQueryable();
        var blocks = new[]
        {
            BlockedUser.Create(caller, iBlocked).Value,
            BlockedUser.Create(blockedMe, caller).Value
        }.AsQueryable();

        var result = SocialGraphReader
            .BuildVisibleCheers(cheers, blocks, caller, isReceived: true, Now.AddDays(-90), 100)
            .ToList();

        result.Should().ContainSingle().Which.SenderId.Should().Be(friend);
    }

    [Fact]
    public void VisibleCheers_Sent_ExcludeBlockedRecipients()
    {
        var caller = Guid.NewGuid();
        var friend = Guid.NewGuid();
        var iBlocked = Guid.NewGuid();

        var cheers = new[]
        {
            Cheer.Create(caller, friend, null, "a").Value,
            Cheer.Create(caller, iBlocked, null, "b").Value,
            Cheer.Create(friend, caller, null, "inbound").Value
        }.AsQueryable();
        var blocks = new[] { BlockedUser.Create(caller, iBlocked).Value }.AsQueryable();

        var result = SocialGraphReader
            .BuildVisibleCheers(cheers, blocks, caller, isReceived: false, Now.AddDays(-90), 100)
            .ToList();

        result.Should().ContainSingle().Which.RecipientId.Should().Be(friend);
    }

    [Fact]
    public void VisibleCheers_IncludeBoundary_ExcludeOlderThanLookback()
    {
        var caller = Guid.NewGuid();
        var since = Now.AddDays(-90);
        var atBoundary = WithCreatedAt(Cheer.Create(Guid.NewGuid(), caller, null, "edge").Value, since);
        var justOlder = WithCreatedAt(Cheer.Create(Guid.NewGuid(), caller, null, "stale").Value, since.AddTicks(-1));

        var result = SocialGraphReader
            .BuildVisibleCheers(new[] { atBoundary, justOlder }.AsQueryable(), Array.Empty<BlockedUser>().AsQueryable(), caller, isReceived: true, since, 100)
            .ToList();

        result.Should().ContainSingle().Which.Note.Should().Be("edge");
    }

    [Fact]
    public void VisibleCheers_OrderNewestFirst_AndCapAtLimit()
    {
        var caller = Guid.NewGuid();
        var newest = WithCreatedAt(Cheer.Create(Guid.NewGuid(), caller, null, "n0").Value, Now);
        var middle = WithCreatedAt(Cheer.Create(Guid.NewGuid(), caller, null, "n1").Value, Now.AddHours(-1));
        var oldest = WithCreatedAt(Cheer.Create(Guid.NewGuid(), caller, null, "n2").Value, Now.AddHours(-2));

        var result = SocialGraphReader
            .BuildVisibleCheers(new[] { middle, oldest, newest }.AsQueryable(), Array.Empty<BlockedUser>().AsQueryable(), caller, isReceived: true, Now.AddDays(-90), 2)
            .ToList();

        result.Should().HaveCount(2);
        result.Select(c => c.Note).Should().ContainInOrder("n0", "n1");
    }
}
