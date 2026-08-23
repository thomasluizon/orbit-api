using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class ClosedMonthRecapStoreTests
{
    [Fact]
    public async Task AddAndFindResponseJson_UsesUserAndResolvedWindowKey()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Recap User", "recap@example.com").Value;
        factory.Context.Users.Add(user);
        var recap = ClosedMonthRecap.Create(
            user.Id,
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 6, 30),
            "{\"period\":\"month\"}").Value;
        var store = new ClosedMonthRecapStore(factory.Context);

        await store.AddAsync(recap, CancellationToken.None);
        await factory.Context.SaveChangesAsync();

        var stored = await store.FindResponseJsonAsync(
            user.Id,
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 6, 30),
            CancellationToken.None);
        var wrongWindow = await store.FindResponseJsonAsync(
            user.Id,
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            CancellationToken.None);

        stored.Should().Be("{\"period\":\"month\"}");
        wrongWindow.Should().BeNull();
    }
}
