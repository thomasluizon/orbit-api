using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AppConfigServiceTests
{
    private static OrbitDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"AppConfigServiceTests_{Guid.NewGuid()}")
            .Options);

    private static async Task SeedAsync(OrbitDbContext dbContext, string key, string value)
    {
        dbContext.Set<AppConfig>().Add(AppConfig.Create(key, value));
        await dbContext.SaveChangesAsync();
    }

    private static AppConfigService Create(OrbitDbContext dbContext) =>
        new(dbContext, new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public async Task GetAsync_MissingKey_ReturnsDefault()
    {
        await using var dbContext = NewDbContext();
        var service = Create(dbContext);

        var value = await service.GetAsync("missing", 42);

        value.Should().Be(42);
    }

    [Fact]
    public async Task GetAsync_IntValue_ParsesFromStore()
    {
        await using var dbContext = NewDbContext();
        await SeedAsync(dbContext, "max_habits", "7");
        var service = Create(dbContext);

        var value = await service.GetAsync("max_habits", 0);

        value.Should().Be(7);
    }

    [Fact]
    public async Task GetAsync_BoolValue_ParsesFromStore()
    {
        await using var dbContext = NewDbContext();
        await SeedAsync(dbContext, "feature_on", "true");
        var service = Create(dbContext);

        var value = await service.GetAsync("feature_on", false);

        value.Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_LongValue_ParsesFromStore()
    {
        await using var dbContext = NewDbContext();
        await SeedAsync(dbContext, "big", "9000000000");
        var service = Create(dbContext);

        var value = await service.GetAsync("big", 0L);

        value.Should().Be(9_000_000_000L);
    }

    [Fact]
    public async Task GetAsync_DoubleValue_ParsesFromStore()
    {
        await using var dbContext = NewDbContext();
        await SeedAsync(dbContext, "ratio", "1.5");
        var service = Create(dbContext);

        var value = await service.GetAsync("ratio", 0d);

        value.Should().Be(1.5d);
    }

    [Fact]
    public async Task GetAsync_StringValue_ReturnsRaw()
    {
        await using var dbContext = NewDbContext();
        await SeedAsync(dbContext, "greeting", "hello");
        var service = Create(dbContext);

        var value = await service.GetAsync("greeting", "default");

        value.Should().Be("hello");
    }

    [Fact]
    public async Task GetAsync_UnparseableValue_ReturnsDefault()
    {
        await using var dbContext = NewDbContext();
        await SeedAsync(dbContext, "max_habits", "not-a-number");
        var service = Create(dbContext);

        var value = await service.GetAsync("max_habits", 5);

        value.Should().Be(5);
    }

    [Fact]
    public async Task GetAsync_UnsupportedType_ReturnsDefault()
    {
        await using var dbContext = NewDbContext();
        await SeedAsync(dbContext, "some_guid", Guid.NewGuid().ToString());
        var service = Create(dbContext);
        var fallback = Guid.NewGuid();

        var value = await service.GetAsync("some_guid", fallback);

        value.Should().Be(fallback);
    }

    [Fact]
    public async Task GetAsync_SecondCall_ServesFromCacheAfterRowRemoved()
    {
        await using var dbContext = NewDbContext();
        await SeedAsync(dbContext, "cached_key", "10");
        var service = Create(dbContext);

        var first = await service.GetAsync("cached_key", 0);

        var row = await dbContext.Set<AppConfig>().FirstAsync(c => c.Key == "cached_key");
        dbContext.Set<AppConfig>().Remove(row);
        await dbContext.SaveChangesAsync();

        var second = await service.GetAsync("cached_key", 0);

        first.Should().Be(10);
        second.Should().Be(10);
    }

    [Fact]
    public async Task GetAsync_MissingKey_CachesDefault()
    {
        await using var dbContext = NewDbContext();
        var service = Create(dbContext);

        var first = await service.GetAsync("later_key", 1);
        await SeedAsync(dbContext, "later_key", "99");
        var second = await service.GetAsync("later_key", 1);

        first.Should().Be(1);
        second.Should().Be(1);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllConfigs()
    {
        await using var dbContext = NewDbContext();
        await SeedAsync(dbContext, "a", "1");
        await SeedAsync(dbContext, "b", "2");
        var service = Create(dbContext);

        var all = await service.GetAllAsync();

        all.Should().HaveCount(2);
        all["a"].Should().Be("1");
        all["b"].Should().Be("2");
    }

    [Fact]
    public async Task GetAllAsync_SecondCall_ServesFromCache()
    {
        await using var dbContext = NewDbContext();
        await SeedAsync(dbContext, "a", "1");
        var service = Create(dbContext);

        var first = await service.GetAllAsync();
        await SeedAsync(dbContext, "b", "2");
        var second = await service.GetAllAsync();

        first.Should().HaveCount(1);
        second.Should().HaveCount(1);
    }
}
