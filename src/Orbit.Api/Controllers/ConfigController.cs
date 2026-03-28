using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Domain.Interfaces;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ConfigController(IAppConfigService configService, ILogger<ConfigController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetConfig(CancellationToken cancellationToken)
    {
        var config = await configService.GetAllAsync(cancellationToken);
        logger.LogInformation("Config fetched");
        return Ok(config);
    }
}
