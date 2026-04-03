using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Controllers;

public class ConfigControllerTests
{
    private readonly IAppConfigService _configService = Substitute.For<IAppConfigService>();
    private readonly ILogger<ConfigController> _logger = Substitute.For<ILogger<ConfigController>>();
    private readonly ConfigController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public ConfigControllerTests()
    {
        _controller = new ConfigController(_configService, _logger);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    // --- GetConfig ---

    [Fact]
    public async Task GetConfig_ReturnsOk()
    {
        var config = new Dictionary<string, string> { { "key", "value" } };
        _configService.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(config);

        var result = await _controller.GetConfig(CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().Be(config);
    }
}
