using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.OAuth;
using Orbit.Application.Marketing.Commands;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public class MarketingControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly MarketingController _controller;

    public MarketingControllerTests()
    {
        _controller = new MarketingController(_mediator);
    }

    [Theory]
    [InlineData("en", true, "You're unsubscribed", "You will no longer receive product-update emails from Orbit.")]
    [InlineData("pt-BR", true, "Inscrição cancelada", "Você não receberá mais e-mails de novidades do Orbit.")]
    [InlineData("en", false, "Invalid link", "This unsubscribe link is invalid or has expired.")]
    [InlineData("pt-BR", false, "Link inválido", "Este link para cancelar inscrição é inválido ou expirou.")]
    public async Task Unsubscribe_UsesSharedLocalizedMessagePage(
        string language, bool success, string title, string message)
    {
        _mediator.Send(Arg.Any<UnsubscribeMarketingCommand>(), Arg.Any<CancellationToken>())
            .Returns(success ? Result.Success() : Result.Failure("Invalid token"));

        var result = await _controller.Unsubscribe("token", language, CancellationToken.None);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().Be("text/html");
        content.StatusCode.Should().Be(success ? null : 400);
        var html = content.Content!;
        html.Should().Contain($"<html lang=\"{language}\">");
        html.Should().Contain(OAuthPageStyles.Css).And.Contain(OAuthPageStyles.Mark);
        html.Should().Contain("--bg: #09090B;")
            .And.Contain("--fg-1: #F4F4F6;").And.Contain("--fg-2: #C9C9CC;");
        html.Should().Contain("--font-sans: 'Geist Sans', system-ui, sans-serif;")
            .And.Contain("/oauth/assets/geist-latin.woff2");
        html.Should().Contain("@media (prefers-color-scheme: light)")
            .And.Contain("--bg: #FAFAFA;").And.Contain("--fg-1: #1A1A1D;")
            .And.Contain("--fg-2: #424247;").And.Contain("content=\"dark light\"");
        html.Should().Contain("<main class=\"card\">");
        html.Should().NotContain("#020618").And.NotContain("#F8FAFC").And.NotContain("#90A1B9")
            .And.NotContain("gradient").And.NotContain("glow");
        var decodedHtml = System.Net.WebUtility.HtmlDecode(html);
        decodedHtml.Should().Contain($"<title>{title}</title>")
            .And.Contain($"<h1>{title}</h1>")
            .And.Contain($"<p class=\"body-line\">{message}</p>");
    }

    [Fact]
    public async Task Unsubscribe_Success_ReturnsHtmlContent()
    {
        _mediator.Send(Arg.Any<UnsubscribeMarketingCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.Unsubscribe("token", null, CancellationToken.None);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().Be("text/html");
    }

    [Fact]
    public async Task Unsubscribe_SuccessPortuguese_ReturnsHtmlContent()
    {
        _mediator.Send(Arg.Any<UnsubscribeMarketingCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.Unsubscribe("token", "pt", CancellationToken.None);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().Be("text/html");
    }

    [Fact]
    public async Task Unsubscribe_Failure_ReturnsBadRequestHtml()
    {
        _mediator.Send(Arg.Any<UnsubscribeMarketingCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Invalid token"));

        var result = await _controller.Unsubscribe("bad", null, CancellationToken.None);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task UnsubscribeOneClick_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<UnsubscribeMarketingCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.UnsubscribeOneClick("token", CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task UnsubscribeOneClick_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<UnsubscribeMarketingCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Invalid token"));

        var result = await _controller.UnsubscribeOneClick("bad", CancellationToken.None);

        result.Should().BeOfType<BadRequestResult>();
    }
}
