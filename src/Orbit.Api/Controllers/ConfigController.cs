using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Domain.Interfaces;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[ApiVersion(1.0)]
[ApiVersion(2.0)]
[Route("api/[controller]")]
public partial class ConfigController(
    IAppConfigService configService,
    IFeatureFlagService featureFlagService,
    ILogger<ConfigController> logger) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<AppConfigResponse>(StatusCodes.Status200OK)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetConfig(CancellationToken cancellationToken)
    {
        var limits = (await configService.GetAllAsync(cancellationToken))
            .Where(config => config.Key != "MaxUserFacts")
            .ToDictionary(config => config.Key, config => config.Value);

        var featureFlags = await featureFlagService.ResolveForUserAsync(HttpContext.GetUserId(), cancellationToken);

        var features = featureFlags.ToDictionary(f => f.Key, f => new FeatureFlagStateResponse(f.Enabled, f.PlanRequirement));

        var minVersion = await configService.GetAsync(
            Orbit.Application.Common.AppConfigKeys.MinSupportedVersion, "0.0.0", cancellationToken);

        LogConfigFetched(logger);
        return Ok(new AppConfigResponse(
            limits,
            features,
            new SyncSettingsResponse(SyncIntervalSeconds: 300, SyncMaxBatchSize: 100),
            minVersion));
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Config fetched")]
    private static partial void LogConfigFetched(ILogger logger);
}

public sealed record AppConfigResponse(
    Dictionary<string, string> Limits,
    Dictionary<string, FeatureFlagStateResponse> Features,
    SyncSettingsResponse Settings,
    string MinVersion);

public sealed record FeatureFlagStateResponse(bool Enabled, string? PlanRequirement);

public sealed record SyncSettingsResponse(int SyncIntervalSeconds, int SyncMaxBatchSize);
