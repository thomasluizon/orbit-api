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
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetConfig(CancellationToken cancellationToken)
    {
        var limits = await configService.GetAllAsync(cancellationToken);

        var featureFlags = await featureFlagService.ResolveForUserAsync(HttpContext.GetUserId(), cancellationToken);

        var features = featureFlags.ToDictionary(f => f.Key, f => new { f.Enabled, f.PlanRequirement });

        var minVersion = await configService.GetAsync(
            Orbit.Application.Common.AppConfigKeys.MinSupportedVersion, "0.0.0", cancellationToken);

        LogConfigFetched(logger);
        return Ok(new
        {
            limits,
            features,
            settings = new { syncIntervalSeconds = 300, syncMaxBatchSize = 100 },
            minVersion
        });
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Config fetched")]
    private static partial void LogConfigFetched(ILogger logger);
}
