using NSubstitute;
using FluentAssertions;
using Orbit.Api.Middleware;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class ChatControllerTests
{
    [Theory]
    [InlineData(false, "pt-BR", null, null)]
    [InlineData(true, "pt-BR", null, null)]
    [InlineData(false, "en-US", null, null)]
    [InlineData(true, "pt-BR;q=0.5,en;q=1", null, null)]
    [InlineData(false, "*", "{\"supportsParts\":true}", null)]
    [InlineData(true, "*", "{\"supportsParts\":true}", null)]
    [InlineData(false, "pt-BR", "{\"locale\":\"en\",\"supportsParts\":true}", "en")]
    [InlineData(true, "en", "{\"locale\":\"pt-BR\",\"supportsParts\":true}", "pt-BR")]
    public async Task Chat_RequestLocalePreservesProfileFallback(bool stream, string header, string? context, string? expected)
    {
        _controller.Request.Headers.AcceptLanguage = header;
        _controller.Response.Body = new MemoryStream();
        _mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ChatResponse("Reply", [])));

        if (stream)
            await _controller.ProcessChatStream("Hello", null, null, default, clientContext: context);
        else
            await _controller.ProcessChat("Hello", null, null, default, clientContext: context);

        await _mediator.Received(1).Send(Arg.Is<ProcessUserChatCommand>(command =>
            (command.ClientContext == null ? null : command.ClientContext.Locale) == expected
            && (context == null || command.ClientContext != null && command.ClientContext.SupportsParts == true)), Arg.Any<CancellationToken>());
        ChatErrorLocalization.PreferredLocale(_controller.HttpContext).Should().Be(expected);
    }
    [Theory]
    [InlineData("en", "pt-BR", false)]
    [InlineData("pt-BR", "en", true)]
    public async Task Chat_ErrorLocalizationUsesRequestBeforeProfile(string locale, string profile, bool portuguese)
    {
        var repository = Substitute.For<IGenericRepository<User>>();
        var user = User.Create("Reader", "reader@example.com").Value;
        user.SetLanguage(profile);
        repository.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ChatResponse("Reply", [])));
        await _controller.ProcessChat("Hello", null, null, default, clientContext: "{\"locale\":\"" + locale + "\"}");

        var resolved = await new RequestLanguageResolver(repository).IsPortugueseAsync(_controller.HttpContext, default);

        resolved.Should().Be(portuguese);
    }

}
