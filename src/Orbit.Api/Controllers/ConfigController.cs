using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Domain.Interfaces;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public partial class ConfigController(IAppConfigService configService, ILogger<ConfigController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetConfig(CancellationToken cancellationToken)
    {
        var config = await configService.GetAllAsync(cancellationToken);
        LogConfigFetched(logger);
        return Ok(config);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Config fetched")]
    private static partial void LogConfigFetched(ILogger logger);
}
