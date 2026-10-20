using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Events;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class IssuedAstraBlockStoreTests
{
    private static IssuedAstraBlock Block(Guid userId) => IssuedAstraBlock.Create(userId,
        new AstraBlockDefinition("choice", "When?", [new("a", "Morning"), new("b", "Evening")], []));

    [Fact]
    public async Task Answer_IsOneShotAndScopedToOwner()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Blocks", "blocks@example.com").Value;
        factory.Context.Users.Add(user);
        await factory.Context.SaveChangesAsync();
        var store = new IssuedAstraBlockStore(factory.Context);
        var block = Block(user.Id);
        await store.AddAsync(block, default);
        (await store.GetAsync(block.Id, Guid.NewGuid(), default)).Should().BeNull();
        var now = block.CreatedAtUtc.AddSeconds(1);
        var claims = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var context = factory.CreateContext();
            return await new IssuedAstraBlockStore(context).MarkAnsweredAsync(block.Id, user.Id, now, default);
        }));
        claims.Should().ContainSingle(value => value);
        (await store.GetAsync(block.Id, user.Id, default))!.AnsweredAtUtc.Should().Be(now);
    }

    [Fact]
    public async Task InvalidateAndExpiry_PreventClaimsWithoutAffectingOtherUsers()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Blocks", "blocks@example.com").Value;
        var other = User.Create("Other", "other@example.com").Value;
        factory.Context.Users.AddRange(user, other);
        await factory.Context.SaveChangesAsync();
        var store = new IssuedAstraBlockStore(factory.Context);
        var block = Block(user.Id);
        var second = Block(user.Id);
        var otherBlock = Block(other.Id);
        await store.AddAsync(block, default);
        await store.AddAsync(second, default);
        await store.AddAsync(otherBlock, default);
        (await store.MarkAnsweredAsync(otherBlock.Id, other.Id, otherBlock.ExpiresAtUtc, default)).Should().BeFalse();
        await store.InvalidateOpenAsync(user.Id, default);
        foreach (var issued in new[] { block, second })
        {
            (await store.MarkAnsweredAsync(issued.Id, user.Id, issued.CreatedAtUtc, default)).Should().BeFalse();
            (await store.GetAsync(issued.Id, user.Id, default))!.InvalidatedAtUtc.Should().NotBeNull();
        }
        (await store.MarkAnsweredAsync(otherBlock.Id, other.Id, otherBlock.CreatedAtUtc, default)).Should().BeTrue();
    }

    [Fact]
    public async Task AnswerRecovery_RequiresTheOwnerAndClaimAndNeverReleasesAStoredReply()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Blocks", "blocks@example.com").Value;
        factory.Context.Users.Add(user);
        await factory.Context.SaveChangesAsync();
        var store = new IssuedAstraBlockStore(factory.Context);
        var block = Block(user.Id);
        await store.AddAsync(block, default);
        var claim = block.CreatedAtUtc;
        var date = new DateOnly(2026, 4, 3);
        (await store.MarkAnsweredAsync(block.Id, user.Id, claim, default)).Should().BeTrue();
        await store.RecordAnswerAsync(block.Id, user.Id, claim,
            IssuedAstraBlock.SerializeAnswer(new(block.Id, ["a"])), date, null, default);
        (await store.ReleaseAnswerAsync(block.Id, Guid.NewGuid(), claim, default)).Should().BeFalse();
        (await store.ReleaseAnswerAsync(block.Id, user.Id, claim.AddSeconds(1), default)).Should().BeFalse();
        (await store.ReleaseAnswerAsync(block.Id, user.Id, claim, default)).Should().BeTrue();
        var released = await store.GetAsync(block.Id, user.Id, default);
        released!.AnsweredAtUtc.Should().BeNull();
        released.AnswerJson.Should().BeNull();
        released.ReservedAllowanceDate.Should().BeNull();
        (await store.ReleaseAnswerAsync(block.Id, user.Id, claim, default)).Should().BeFalse();

        var retryClaim = claim.AddSeconds(2);
        (await store.MarkAnsweredAsync(block.Id, user.Id, retryClaim, default)).Should().BeTrue();
        await store.RecordAnswerAsync(block.Id, user.Id, retryClaim,
            IssuedAstraBlock.SerializeAnswer(new(block.Id, ["b"])), date, null, default);
        (await store.CompleteAnswerAsync(block.Id, Guid.NewGuid(), retryClaim, "{}", default)).Should().BeFalse();
        (await store.CompleteAnswerAsync(block.Id, user.Id, claim, "{}", default)).Should().BeFalse();
        (await store.CompleteAnswerAsync(block.Id, user.Id, retryClaim, "{}", default)).Should().BeTrue();
        (await store.CompleteAnswerAsync(block.Id, user.Id, retryClaim, "{\"other\":true}", default)).Should().BeFalse();
        (await store.ReleaseAnswerAsync(block.Id, user.Id, retryClaim, default)).Should().BeFalse();
        (await store.GetAsync(block.Id, user.Id, default))!.ResponseJson.Should().Be("{}");
    }

    [Fact]
    public async Task InFlightAnswer_InvalidatedByLaterTurn_DoesNotReopenOnRelease()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Blocks", "blocks@example.com").Value;
        factory.Context.Users.Add(user);
        await factory.Context.SaveChangesAsync();
        var store = new IssuedAstraBlockStore(factory.Context);
        var block = Block(user.Id);
        await store.AddAsync(block, default);
        var claim = block.CreatedAtUtc;
        (await store.MarkAnsweredAsync(block.Id, user.Id, claim, default)).Should().BeTrue();
        await store.RecordAnswerAsync(block.Id, user.Id, claim,
            IssuedAstraBlock.SerializeAnswer(new(block.Id, ["a"])), null, null, default);
        await store.InvalidateOpenAsync(user.Id, default);
        (await store.ReleaseAnswerAsync(block.Id, user.Id, claim, default)).Should().BeTrue();
        (await store.GetAsync(block.Id, user.Id, default))!.InvalidatedAtUtc.Should().NotBeNull();
        (await store.MarkAnsweredAsync(block.Id, user.Id, claim.AddSeconds(1), default)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AccountResetAndDeletion_RemoveBlocks(bool reset)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Blocks", "blocks@example.com").Value;
        factory.Context.Users.Add(user);
        await factory.Context.SaveChangesAsync();
        await new IssuedAstraBlockStore(factory.Context).AddAsync(Block(user.Id), default);
        if (reset)
            await new AccountResetRepository(factory.Context, Substitute.For<IAccountEventCollector>()).DeleteAllUserDataAsync(user.Id);
        else
            await factory.Context.Users.Where(item => item.Id == user.Id).ExecuteDeleteAsync();
        (await factory.Context.IssuedAstraBlocks.CountAsync()).Should().Be(0);
    }
    [Fact]
    public async Task LegacyAnsweredBlock_WithoutRecordedAnswer_CannotBeReleased()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Blocks", "blocks@example.com").Value;
        factory.Context.Users.Add(user);
        var block = Block(user.Id);
        block.MarkAnswered(block.CreatedAtUtc);
        factory.Context.IssuedAstraBlocks.Add(block);
        await factory.Context.SaveChangesAsync();
        var store = new IssuedAstraBlockStore(factory.Context);

        (await store.ReleaseAnswerAsync(block.Id, user.Id, block.CreatedAtUtc, default)).Should().BeFalse();
        (await store.GetAsync(block.Id, user.Id, default))!.AnsweredAtUtc.Should().Be(block.CreatedAtUtc);
    }

}
