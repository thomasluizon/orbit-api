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
}
