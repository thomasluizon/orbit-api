using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public partial class ConfigController(
    IAppConfigService configService,
    OrbitDbContext dbContext,
    ILogger<ConfigController> logger) : ControllerBase
{
    public record FeatureFlagDto(string Key, bool Enabled, string? PlanRequirement);

    [HttpGet]
    public async Task<IActionResult> GetConfig(CancellationToken cancellationToken)
    {
        var limits = await configService.GetAllAsync(cancellationToken);

        var featureFlags = await dbContext.AppFeatureFlags
            .AsNoTracking()
            .Select(f => new FeatureFlagDto(f.Key, f.Enabled, f.PlanRequirement))
            .ToListAsync(cancellationToken);

        var features = featureFlags.ToDictionary(f => f.Key, f => new { f.Enabled, f.PlanRequirement });

        LogConfigFetched(logger);
        return Ok(new
        {
            limits,
            features,
            settings = new { syncIntervalSeconds = 300, syncMaxBatchSize = 100 }
        });
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Config fetched")]
    private static partial void LogConfigFetched(ILogger logger);
}
