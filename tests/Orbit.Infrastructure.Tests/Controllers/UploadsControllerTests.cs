using System.Security.Claims;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using System.Reflection;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Uploads.Commands;
using Orbit.Application.Uploads.Queries;
using Orbit.Api.RateLimiting;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Controllers;

public class UploadsControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ILogger<UploadsController> _logger = Substitute.For<ILogger<UploadsController>>();
    private readonly IObjectStorageReadService _objectStorage = Substitute.For<IObjectStorageReadService>();
    private readonly UploadsController _controller;
    private static readonly Guid UserId = Guid.Parse("2b3b1fc7-813c-4eb6-ad02-bd7754299703");

    public UploadsControllerTests()
    {
        _mediator.Send(Arg.Any<UploadOwnerExistsQuery>(), Arg.Any<CancellationToken>()).Returns(true);
        _controller = new UploadsController(_mediator, _objectStorage, _logger);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    [Fact]
    public async Task SignUpload_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<SignUploadCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SignUploadResponse("key", "https://signed", "https://public")));

        var request = new UploadsController.SignUploadRequest("image/png", 1024);
        var result = await _controller.SignUpload(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task SignUpload_ReturnedObjectUrl_IsReadableByOwnerAndPrivateToThatAccount()
    {
        using var client = new AmazonS3Client(new BasicAWSCredentials("example-access", "example-secret"), RegionEndpoint.USEast2);
        var storage = new S3ObjectStorageService(client, Options.Create(new S3StorageSettings
        {
            Bucket = "orbit-test-uploads",
            PublicBaseUrl = "https://api.example.com"
        }));
        _mediator.Send(Arg.Any<SignUploadCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
            new SignUploadCommandHandler(storage).Handle(call.Arg<SignUploadCommand>(), default));
        var controller = new UploadsController(_mediator, storage, _logger) { ControllerContext = _controller.ControllerContext };

        var signed = (await controller.SignUpload(new("image/png", 1024), default)).Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<SignUploadResponse>().Subject;
        var url = new Uri(signed.PublicUrl);
        var parts = url.AbsolutePath.Split('/');
        url.AbsolutePath.Should().Be($"/api/uploads/object/{signed.Key}");
        var read = (await controller.ReadObject(parts[^2], parts[^1], default)).Should().BeOfType<RedirectResult>().Subject;
        new Uri(read.Url!).AbsolutePath.Should().EndWith(signed.Key);
        read.Url.Should().Contain("X-Amz-Signature=");
        controller.Response.Headers.CacheControl.ToString().Should().Be("no-store");

        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "Bearer"));
        (await controller.ReadObject(parts[^2], parts[^1], default)).Should().BeOfType<NotFoundResult>();
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        (await controller.ReadObject(parts[^2], parts[^1], default)).Should().BeOfType<UnauthorizedResult>();
        await _mediator.Received(1).Send(Arg.Is<UploadOwnerExistsQuery>(query => query.UserId == UserId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignUpload_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<SignUploadCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<SignUploadResponse>("Image too large"));

        var request = new UploadsController.SignUploadRequest("image/png", 99999999);
        var result = await _controller.SignUpload(request, CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task SignUpload_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<SignUploadCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<SignUploadResponse>("Pro required"));

        var request = new UploadsController.SignUploadRequest("image/png", 1024);
        var result = await _controller.SignUpload(request, CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task ReadObject_ValidMintedKey_RedirectsToFreshReadUrl()
    {
        const string userId = "2b3b1fc7-813c-4eb6-ad02-bd7754299703";
        const string fileName = "1e21a217-e146-43c9-b61c-b5871a991578.webp";
        var key = $"{userId}/{fileName}";
        _objectStorage.CreateReadUrlAsync(key, Arg.Any<CancellationToken>())
            .Returns("https://example.s3.amazonaws.com/signed-read");

        var result = await _controller.ReadObject(userId, fileName, CancellationToken.None);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("https://example.s3.amazonaws.com/signed-read");
        _controller.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        await _objectStorage.Received(1).CreateReadUrlAsync(key, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadObject_ParsedGuids_UseCanonicalObjectKey()
    {
        const string userId = "2B3B1FC7-813C-4EB6-AD02-BD7754299703";
        const string fileName = "1E21A217-E146-43C9-B61C-B5871A991578.webp";
        const string canonicalKey = "2b3b1fc7-813c-4eb6-ad02-bd7754299703/1e21a217-e146-43c9-b61c-b5871a991578.webp";
        _objectStorage.CreateReadUrlAsync(canonicalKey, Arg.Any<CancellationToken>())
            .Returns("https://example.s3.amazonaws.com/signed-read");

        var result = await _controller.ReadObject(userId, fileName, CancellationToken.None);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("https://example.s3.amazonaws.com/signed-read");
        await _objectStorage.Received(1).CreateReadUrlAsync(canonicalKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadObject_MissingOwner_ReturnsNotFoundWithoutPresigning()
    {
        _mediator.Send(Arg.Any<UploadOwnerExistsQuery>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _controller.ReadObject(UserId.ToString(), $"{Guid.NewGuid()}.png", CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        await _mediator.Received(1).Send(Arg.Is<UploadOwnerExistsQuery>(query => query.UserId == UserId), Arg.Any<CancellationToken>());
        await _objectStorage.DidNotReceive().CreateReadUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("../other", "1e21a217-e146-43c9-b61c-b5871a991578.webp")]
    [InlineData("2b3b1fc7-813c-4eb6-ad02-bd7754299703", "../other.webp")]
    [InlineData("2b3b1fc7-813c-4eb6-ad02-bd7754299703", "1e21a217-e146-43c9-b61c-b5871a991578.pdf")]
    public async Task ReadObject_InvalidKey_ReturnsNotFoundWithoutPresigning(string userId, string fileName)
    {
        var result = await _controller.ReadObject(userId, fileName, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        await _objectStorage.DidNotReceive().CreateReadUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<UploadOwnerExistsQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadObject_OtherOwner_ReturnsNotFoundWithoutLookupOrPresigning()
    {
        var result = await _controller.ReadObject(Guid.NewGuid().ToString(), $"{Guid.NewGuid()}.png", CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        await _mediator.DidNotReceive().Send(Arg.Any<UploadOwnerExistsQuery>(), Arg.Any<CancellationToken>());
        await _objectStorage.DidNotReceive().CreateReadUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadObject_Anonymous_ReturnsUnauthorized()
    {
        _controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        var result = await _controller.ReadObject(UserId.ToString(), $"{Guid.NewGuid()}.png", CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
        await _objectStorage.DidNotReceive().CreateReadUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ReadObject_RequiresAuthenticationAndDistributedRateLimit()
    {
        var method = typeof(UploadsController).GetMethod(nameof(UploadsController.ReadObject))
            ?? throw new InvalidOperationException("ReadObject action is missing.");

        method.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();
        typeof(UploadsController).GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull();
        method.GetCustomAttribute<EnableRateLimitingAttribute>().Should().BeNull();
        method.GetCustomAttribute<DistributedRateLimitAttribute>().Should().NotBeNull();
    }
}
