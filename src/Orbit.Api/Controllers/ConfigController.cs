using Microsoft.AspNetCore.Mvc;
using Orbit.Domain.Interfaces;

namespace Orbit.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConfigController(IAppConfigService configService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetConfig(CancellationToken cancellationToken)
    {
        var config = await configService.GetAllAsync(cancellationToken);
        return Ok(config);
    }
}
