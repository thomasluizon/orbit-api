using System.Security.Claims;
using System.Net;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Subscriptions;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Application.Subscriptions.Queries;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public class SubscriptionControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ILogger<SubscriptionController> _logger = Substitute.For<ILogger<SubscriptionController>>();
    private readonly SubscriptionController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public SubscriptionControllerTests()
    {
        _controller = new SubscriptionController(_mediator, _logger);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        var httpContext = new DefaultHttpContext { User = principal };
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    // --- CreateCheckout ---

    [Fact]
    public async Task CreateCheckout_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<CreateCheckoutCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(CheckoutResponse)!));

        var request = new SubscriptionController.CreateCheckoutRequest("month");
        var result = await _controller.CreateCheckout(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task CreateCheckout_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<CreateCheckoutCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<CheckoutResponse>("Pro required"));

        var request = new SubscriptionController.CreateCheckoutRequest("month");
        var result = await _controller.CreateCheckout(request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task CreateCheckout_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<CreateCheckoutCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<CheckoutResponse>("Invalid interval"));

        var request = new SubscriptionController.CreateCheckoutRequest("invalid");
        var result = await _controller.CreateCheckout(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateCheckout_UsesForwardedGeoHeaders()
    {
        _controller.HttpContext.Request.Headers["X-Forwarded-For"] = "203.0.113.10, 10.0.0.1";
        _controller.HttpContext.Request.Headers["CF-IPCountry"] = "br";
        _mediator.Send(Arg.Any<CreateCheckoutCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(CheckoutResponse)!));

        var request = new SubscriptionController.CreateCheckoutRequest("month");
        await _controller.CreateCheckout(request, CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<CreateCheckoutCommand>(command =>
                command.CountryCode == "BR" &&
                command.IpAddress == "203.0.113.10"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateCheckout_UsesForwardedCountryAndIp()
    {
        _mediator.Send(Arg.Any<CreateCheckoutCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(CheckoutResponse)!));

        _controller.HttpContext.Request.Headers["CF-IPCountry"] = "br";
        _controller.HttpContext.Request.Headers["X-Forwarded-For"] = "201.10.20.30, 10.0.0.1";

        var request = new SubscriptionController.CreateCheckoutRequest("month");

        await _controller.CreateCheckout(request, CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<CreateCheckoutCommand>(c => c.CountryCode == "BR" && c.IpAddress == "201.10.20.30"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateCheckout_UsesAcceptLanguageCountryFallback()
    {
        _mediator.Send(Arg.Any<CreateCheckoutCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(CheckoutResponse)!));

        _controller.HttpContext.Request.Headers["Accept-Language"] = "pt-BR,pt;q=0.9,en;q=0.8";

        var request = new SubscriptionController.CreateCheckoutRequest("month");

        await _controller.CreateCheckout(request, CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<CreateCheckoutCommand>(c => c.CountryCode == "BR" && c.IpAddress == "127.0.0.1"),
            Arg.Any<CancellationToken>());
    }

    // --- CreatePortal ---

    [Fact]
    public async Task CreatePortal_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<CreatePortalSessionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(PortalResponse)!));

        var result = await _controller.CreatePortal(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task CreatePortal_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<CreatePortalSessionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<PortalResponse>("Pro required"));

        var result = await _controller.CreatePortal(CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    // --- GetStatus ---

    [Fact]
    public async Task GetStatus_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetSubscriptionStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(SubscriptionStatusResponse)!));

        var result = await _controller.GetStatus(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetStatus_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<GetSubscriptionStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<SubscriptionStatusResponse>("Pro required"));

        var result = await _controller.GetStatus(CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    // --- GetBillingDetails ---

    [Fact]
    public async Task GetBillingDetails_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetBillingDetailsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(BillingDetailsResponse)!));

        var result = await _controller.GetBillingDetails(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetBillingDetails_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<GetBillingDetailsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<BillingDetailsResponse>("Pro required"));

        var result = await _controller.GetBillingDetails(CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    // --- GetPlans ---

    [Fact]
    public async Task GetPlans_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetPlansQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(PlansResponse)!));

        var result = await _controller.GetPlans(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetPlans_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<GetPlansQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<PlansResponse>("Pro required"));

        var result = await _controller.GetPlans(CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task GetPlans_UsesRemoteIpWhenForwardedHeadersMissing()
    {
        _mediator.Send(Arg.Any<GetPlansQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(PlansResponse)!));

        await _controller.GetPlans(CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<GetPlansQuery>(query =>
                query.CountryCode == null &&
                query.IpAddress == "127.0.0.1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPlans_UsesForwardedCountryAndIp()
    {
        _mediator.Send(Arg.Any<GetPlansQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(PlansResponse)!));

        _controller.HttpContext.Request.Headers["X-Vercel-IP-Country"] = "br";
        _controller.HttpContext.Request.Headers["CF-Connecting-IP"] = "177.55.44.33";

        await _controller.GetPlans(CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<GetPlansQuery>(q => q.CountryCode == "BR" && q.IpAddress == "177.55.44.33"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPlans_UsesAcceptLanguageCountryFallback()
    {
        _mediator.Send(Arg.Any<GetPlansQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(PlansResponse)!));

        _controller.HttpContext.Request.Headers["Accept-Language"] = "pt-BR,pt;q=0.9,en;q=0.8";

        await _controller.GetPlans(CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<GetPlansQuery>(query =>
                query.CountryCode == "BR" &&
                query.IpAddress == "127.0.0.1"),
            Arg.Any<CancellationToken>());
    }

    // --- ClaimAdReward ---

    [Fact]
    public async Task ClaimAdReward_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<ClaimAdRewardCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(AdRewardResponse)!));

        var result = await _controller.ClaimAdReward(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ClaimAdReward_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<ClaimAdRewardCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<AdRewardResponse>("Pro required"));

        var result = await _controller.ClaimAdReward(CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    // --- HandleWebhook ---

    [Fact]
    public async Task HandleWebhook_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<HandleWebhookCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{}"));
        httpContext.Request.Headers["Stripe-Signature"] = "test-sig";
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var result = await _controller.HandleWebhook(CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task HandleWebhook_Failure_Returns500()
    {
        _mediator.Send(Arg.Any<HandleWebhookCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Invalid signature"));

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{}"));
        httpContext.Request.Headers["Stripe-Signature"] = "test-sig";
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var result = await _controller.HandleWebhook(CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(500);
    }
}
