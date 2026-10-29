using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class AstraEvalUnmeteredFlagTests
{
    private const string FlagKey = "astra_eval_unmetered";

    [Theory]
    [InlineData("Staging", false, false, true, true)]
    [InlineData("Staging", true, false, true, true)]
    [InlineData("Staging", false, true, false, false)]
    [InlineData("Staging", true, true, false, true)]
    [InlineData("Production", false, false, true, false)]
    [InlineData("Production", true, false, true, false)]
    [InlineData("Production", false, true, true, true)]
    [InlineData("Production", true, true, true, true)]
    [InlineData("Production", false, true, false, false)]
    [InlineData("Production", true, true, false, true)]
    [InlineData("Development", false, true, true, false)]
    [InlineData("Development", true, true, true, false)]
    [InlineData("Custom", true, true, true, false)]
    public async Task Resolver_RestrictsUnmeteredToStagingOrListedProductionAccountsAcrossEveryReadPath(
        string environmentName, bool global, bool listed, bool targeted, bool enabled)
    {
        var host = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environmentName });
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"unmetered-{Guid.NewGuid()}").Options;
        using var db = new OrbitDbContext(options);
        var account = User.Create("Eval", "eval@example.com").Value;
        var other = User.Create("Other", "other@example.com").Value;
        db.Users.AddRange(account, other);
        db.AppFeatureFlags.Add(AppFeatureFlag.Create(FlagKey, global));
        if (targeted)
            db.AppFeatureFlagAccounts.Add(AppFeatureFlagAccount.Create(FlagKey, account.Id));
        host.Configuration["StagingEval:AccountIds"] = listed ? $"invalid, {account.Id}, {Guid.NewGuid()}" : null;
        await db.SaveChangesAsync();
        using var services = new ServiceCollection()
            .AddSingleton<IHostEnvironment>(host.Environment)
            .AddSingleton<IConfiguration>(host.Configuration)
            .AddSingleton(db).AddMemoryCache()
            .AddScoped<IFeatureFlagService, Orbit.Infrastructure.Services.FeatureFlagService>()
            .BuildServiceProvider();
        using var scope = services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IFeatureFlagService>();

        resolver.ResolveForUser(account.Id).Single(flag => flag.Key == FlagKey).Enabled.Should().Be(enabled);
        (await resolver.ResolveForUserAsync(account.Id)).Single(flag => flag.Key == FlagKey).Enabled.Should().Be(enabled);
        (await resolver.GetEnabledKeysForUserAsync(account.Id)).Contains(FlagKey).Should().Be(enabled);
        (await resolver.GetEnabledKeysForUserAsync(account.Id, forceRefresh: true)).Contains(FlagKey).Should().Be(enabled);
        var batch = await resolver.GetEnabledKeysForUsersAsync([account, other]);
        batch[account.Id].Contains(FlagKey).Should().Be(enabled);
        batch[other.Id].Contains(FlagKey).Should().Be(environmentName == "Staging" && global);

        var config = Substitute.For<IAppConfigService>();
        config.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<string, string>());
        var controller = new ConfigController(config, resolver, NullLogger<ConfigController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, account.Id.ToString())], "Test"))
                }
            }
        };
        var response = (OkObjectResult)await controller.GetConfig(default);
        var json = JsonSerializer.SerializeToElement(response.Value, JsonSerializerOptions.Web);
        var feature = json.GetProperty("features").GetProperty(FlagKey);
        feature.GetProperty("enabled").GetBoolean().Should().Be(enabled);
        feature.GetProperty("planRequirement").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
