using System.Text;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Profile.Queries;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public class PublicProfileControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IDistributedCache _cache = Substitute.For<IDistributedCache>();
    private readonly PublicProfileController _controller;

    public PublicProfileControllerTests()
    {
        _controller = new PublicProfileController(_mediator, _cache);
        _cache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((byte[]?)null);
    }

    [Fact]
    public async Task GetPublicProfile_CacheMissSuccess_ReturnsJsonContent()
    {
        _mediator.Send(Arg.Any<GetPublicProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new PublicProfileView("Name", null, null, null, null, null, null, null, null)));

        var result = await _controller.GetPublicProfile("slug", CancellationToken.None);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task GetPublicProfile_CacheMissFailure_ReturnsNotFound()
    {
        _mediator.Send(Arg.Any<GetPublicProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<PublicProfileView>("User not found"));

        var result = await _controller.GetPublicProfile("missing", CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetPublicProfile_CacheHit_ReturnsJsonWithoutQuerying()
    {
        _cache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes("{}"));

        var result = await _controller.GetPublicProfile("slug", CancellationToken.None);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().Be("application/json");
        await _mediator.DidNotReceive().Send(Arg.Any<GetPublicProfileQuery>(), Arg.Any<CancellationToken>());
    }
}
