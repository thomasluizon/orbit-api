using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Orbit.Infrastructure.Tests.Persistence;

public class CommandCounterWriteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonQueryCommands_AreCountedAndCaptured(bool asynchronous)
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        counter.Reset();
        if (asynchronous)
            await factory.Context.Database.ExecuteSqlRawAsync("UPDATE \"Habits\" SET \"Title\" = \"Title\"");
        else
            factory.Context.Database.ExecuteSqlRaw("UPDATE \"Habits\" SET \"Title\" = \"Title\"");
        counter.CommandCount.Should().Be(1);
        counter.SelectCommandCount.Should().Be(0);
        counter.Commands.Should().ContainSingle().Which.Sql.Should().StartWith("UPDATE");
        counter.Reset();
        counter.Commands.Should().BeEmpty();
        counter.CommandCount.Should().Be(0);
    }
    [Fact]
    public async Task CteAndTaggedReads_AreSelectsAndCteUpdatesAreWrites()
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        counter.Reset();
        await factory.Context.Database.ExecuteSqlRawAsync("WITH rows AS (SELECT 1) SELECT * FROM rows");
        await factory.Context.Database.ExecuteSqlRawAsync("-- schedule page\nSELECT 1");
        await factory.Context.Database.ExecuteSqlRawAsync("WITH rows AS (SELECT 1) UPDATE \"Habits\" SET \"Title\" = \"Title\"");
        counter.SelectCommandCount.Should().Be(2);
        counter.WriteCommandCount.Should().Be(1);
    }

}
