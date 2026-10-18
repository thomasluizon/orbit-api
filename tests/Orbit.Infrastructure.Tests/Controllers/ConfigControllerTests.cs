using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Services;
using System.Text.Json;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Controllers;

public class ConfigControllerTests : IDisposable
{
    private readonly IAppConfigService _configService = Substitute.For<IAppConfigService>();
    private readonly ILogger<ConfigController> _logger = Substitute.For<ILogger<ConfigController>>();
    private readonly OrbitDbContext _dbContext;
    private readonly ConfigController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public ConfigControllerTests()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(databaseName: $"ConfigControllerTests_{Guid.NewGuid()}")
            .Options;
        _dbContext = new OrbitDbContext(options);
        _dbContext.Database.EnsureCreated();

        _controller = new ConfigController(_configService, new FeatureFlagService(_dbContext, new MemoryCache(new MemoryCacheOptions())), _logger);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetConfig_ResolvesForCallerAndPreservesFeatureContract()
    {
        var accountA = User.Create("Account A", "a@example.com").Value;
        var accountB = User.Create("Account B", "b@example.com").Value;
        _dbContext.Users.AddRange(accountA, accountB);
        _dbContext.AppFeatureFlagAccounts.Add(AppFeatureFlagAccount.Create("redesign", accountA.Id));
        await _dbContext.SaveChangesAsync();

        foreach (var account in new[] { accountA, accountB })
        {
            _controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, account.Id.ToString())], "Test"));
            var result = (OkObjectResult)await _controller.GetConfig(CancellationToken.None);
            var json = JsonSerializer.SerializeToElement(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var flag = json.GetProperty("features").GetProperty("redesign");
            flag.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("enabled", "planRequirement");
            flag.GetProperty("enabled").GetBoolean().Should().Be(account.Id == accountA.Id);
            flag.GetProperty("planRequirement").ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task GetConfig_SerializesRecordedResponseForUserWithFlags()
    {
        var account = User.Create("Account", "config@example.com").Value;
        _dbContext.Users.Add(account);
        _dbContext.AppFeatureFlags.RemoveRange(_dbContext.AppFeatureFlags
            .Where(flag => flag.Key != "ai_summary" && flag.Key != "offline_mode" && flag.Key != "redesign"));
        (await _dbContext.AppFeatureFlags.SingleAsync(flag => flag.Key == "ai_summary")).SetEnabled(false);
        _dbContext.AppFeatureFlagAccounts.Add(AppFeatureFlagAccount.Create("redesign", account.Id));
        await _dbContext.SaveChangesAsync();
        _controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, account.Id.ToString())], "Test"));
        _configService.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["MaxHabits"] = "5", ["MinSupportedVersion"] = "1.4.2" });
        _configService.GetAsync(AppConfigKeys.MinSupportedVersion, "0.0.0", Arg.Any<CancellationToken>())
            .Returns("1.4.2");

        var result = (OkObjectResult)await _controller.GetConfig(CancellationToken.None);
        var json = JsonSerializer.SerializeToUtf8Bytes(result.Value, JsonSerializerOptions.Web);

        json.Should().Equal("""{"limits":{"MaxHabits":"5","MinSupportedVersion":"1.4.2"},"features":{"ai_summary":{"enabled":false,"planRequirement":"Pro"},"offline_mode":{"enabled":true,"planRequirement":null},"redesign":{"enabled":true,"planRequirement":null}},"settings":{"syncIntervalSeconds":300,"syncMaxBatchSize":100},"minVersion":"1.4.2"}"""u8.ToArray(), "the serialized response is {0}", System.Text.Encoding.UTF8.GetString(json));
    }

    [Fact]
    public void GeneratedOpenApi_ConfigResponseDeclaresCompleteSchema()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../..", "src/Orbit.Api/openapi.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var response = document.RootElement.GetProperty("paths").GetProperty("/api/Config")
            .GetProperty("get").GetProperty("responses").GetProperty("200");

        response.TryGetProperty("content", out var content).Should().BeTrue(
            "the config success response must declare a JSON schema");
        content.GetProperty("application/json").GetProperty("schema").GetProperty("$ref")
            .GetString().Should().Be("#/components/schemas/AppConfigResponse");
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var config = schemas.GetProperty("AppConfigResponse");
        config.GetProperty("required").EnumerateArray().Select(field => field.GetString())
            .Should().BeEquivalentTo("limits", "features", "settings", "minVersion");
        var properties = config.GetProperty("properties");
        properties.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("limits", "features", "settings", "minVersion");
        var limits = properties.GetProperty("limits");
        limits.GetProperty("type").GetString().Should().Be("object");
        limits.GetProperty("additionalProperties").GetProperty("type").GetString().Should().Be("string");
        var features = properties.GetProperty("features");
        features.GetProperty("type").GetString().Should().Be("object");
        features.GetProperty("additionalProperties").GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/FeatureFlagStateResponse");
        properties.GetProperty("settings").GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/SyncSettingsResponse");
        properties.GetProperty("minVersion").GetProperty("type").GetString().Should().Be("string");

        var flag = schemas.GetProperty("FeatureFlagStateResponse");
        flag.GetProperty("required").EnumerateArray().Select(field => field.GetString())
            .Should().BeEquivalentTo("enabled", "planRequirement");
        var flagProperties = flag.GetProperty("properties");
        flagProperties.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("enabled", "planRequirement");
        flagProperties.GetProperty("enabled").GetProperty("type").GetString().Should().Be("boolean");
        flagProperties.GetProperty("planRequirement").GetProperty("type").EnumerateArray()
            .Select(type => type.GetString()).Should().BeEquivalentTo("string", "null");

        var settings = schemas.GetProperty("SyncSettingsResponse");
        settings.GetProperty("required").EnumerateArray().Select(field => field.GetString())
            .Should().BeEquivalentTo("syncIntervalSeconds", "syncMaxBatchSize");
        var settingsProperties = settings.GetProperty("properties");
        settingsProperties.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("syncIntervalSeconds", "syncMaxBatchSize");
        foreach (var setting in settingsProperties.EnumerateObject())
        {
            setting.Value.GetProperty("type").EnumerateArray().Select(type => type.GetString())
                .Should().BeEquivalentTo("integer", "string");
            setting.Value.GetProperty("format").GetString().Should().Be("int32");
        }
    }

    [Fact]
    public void GetConfig_DisablesResponseCaching()
    {
        var cache = typeof(ConfigController).GetMethod(nameof(ConfigController.GetConfig))!
            .GetCustomAttributes(typeof(ResponseCacheAttribute), false).Cast<ResponseCacheAttribute>().Single();
        cache.NoStore.Should().BeTrue();
        cache.Location.Should().Be(ResponseCacheLocation.None);
    }

    [Fact]
    public async Task GetConfig_ReturnsOk()
    {
        var config = new Dictionary<string, string> { { "key", "value" } };
        _configService.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(config);

        var result = await _controller.GetConfig(CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task GetConfig_ExposesMinVersionFromConfigService()
    {
        _configService.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());
        _configService
            .GetAsync(AppConfigKeys.MinSupportedVersion, "0.0.0", Arg.Any<CancellationToken>())
            .Returns("1.4.2");

        var result = await _controller.GetConfig(CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.SerializeToElement(okResult.Value, JsonSerializerOptions.Web);
        json.GetProperty("minVersion").GetString().Should().Be("1.4.2");
    }
}
