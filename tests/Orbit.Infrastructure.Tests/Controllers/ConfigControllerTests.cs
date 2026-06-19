using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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

        _controller = new ConfigController(_configService, _dbContext, _logger);
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
        var minVersion = okResult.Value!
            .GetType()
            .GetProperty("minVersion")!
            .GetValue(okResult.Value);
        minVersion.Should().Be("1.4.2");
    }
}
