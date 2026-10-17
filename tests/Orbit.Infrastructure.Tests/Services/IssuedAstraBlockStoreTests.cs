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
}
