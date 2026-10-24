using System.Linq.Expressions;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Authentication;
using Orbit.Api.Controllers;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Notifications.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Controllers;

public class ChatControllerScopeTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IImageValidationService _imageValidation = Substitute.For<IImageValidationService>();
    private readonly ChatResponse _chatResponse = new("Private habit context", []);

    public static TheoryData<string, string> DeniedCredentials => Cases(
        "", AgentScopes.CatalogRead, AgentScopes.ReadHabits, "chat_interact_extra");

    public static TheoryData<string, string> AllowedCredentials => Cases(
        "jwt", AgentScopes.ChatInteract, "CHAT_INTERACT", "read_only_chat");

    private static TheoryData<string, string> Cases(params string[] credentials)
    {
        var cases = new TheoryData<string, string>();
        foreach (var route in new[] { "chat", "stream", "records", "transcribe" })
            foreach (var credential in credentials)
            {
                cases.Add(route, credential);
                if (credential != "jwt")
                    cases.Add(route, "oauth:" + credential);
            }
        return cases;
    }

    [Theory]
    [MemberData(nameof(DeniedCredentials))]
    public async Task ChatRoute_KeyWithoutChatScope_ForbidsBeforeDispatch(string route, string credential)
    {
        var controller = await CreateControllerAsync(route, credential);

        var result = await InvokeAsync(controller, route);

        result.Should().BeOfType<ForbidResult>();
        _mediator.ReceivedCalls().Should().BeEmpty();
        _imageValidation.ReceivedCalls().Should().BeEmpty();
        controller.Response.Body.Length.Should().Be(0);
        controller.Response.ContentType.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(AllowedCredentials))]
    public async Task ChatRoute_JwtOrChatScopedKey_PreservesBehavior(string route, string credential)
    {
        var controller = await CreateControllerAsync(route, credential);

        var result = await InvokeAsync(controller, route);

        if (route == "stream")
        {
            result.Should().BeOfType<EmptyResult>();
            controller.Response.ContentType.Should().Be("text/event-stream");
            controller.Response.Body.Position = 0;
            var body = await new StreamReader(controller.Response.Body).ReadToEndAsync();
            body.Should().Contain(_chatResponse.AiMessage);
        }
        else
        {
            var value = result.Should().BeOfType<OkObjectResult>().Which.Value;
            if (route == "chat")
                value.Should().Be(_chatResponse);
            else if (route == "records")
                value.Should().BeOfType<RecordListCard>().Which.Items.Single().Title.Should().Be("Private notification");
            else
                value.Should().BeOfType<TranscribeAudioResponse>().Which.Text.Should().Be("Hello");
        }

        if (route is "chat" or "stream")
        {
            await _mediator.Received(1).Send(Arg.Is<ProcessUserChatCommand>(command =>
                command.UserId == UserId &&
                command.AuthMethod == (credential == "jwt" ? AgentAuthMethod.Jwt
                    : credential.StartsWith("oauth:") ? AgentAuthMethod.OAuth : AgentAuthMethod.ApiKey) &&
                command.IsReadOnlyCredential == credential.EndsWith("read_only_chat")), Arg.Any<CancellationToken>());
        }
        else if (route == "records")
        {
            await _mediator.Received(1).Send(
                new GetNotificationsQuery(UserId, RecordListCardBuilder.PageSize, RecordListCardBuilder.PageSize),
                Arg.Any<CancellationToken>());
        }
        else
        {
            await _mediator.Received(1).Send(Arg.Is<TranscribeAudioCommand>(command => command.UserId == UserId),
                Arg.Any<CancellationToken>());
        }
    }

    private async Task<ChatController> CreateControllerAsync(string route, string credential)
    {
        _mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(_chatResponse));
        _mediator.Send(Arg.Any<TranscribeAudioCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new TranscribeAudioResponse("Hello")));
        var handler = new GetRecordListPageQueryHandler(_mediator);
        _mediator.Send(Arg.Any<GetRecordListPageQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<GetRecordListPageQuery>(), call.Arg<CancellationToken>()));
        _mediator.Send(Arg.Any<GetNotificationsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GetNotificationsResponse(
                [new NotificationItemDto(Guid.NewGuid(), "Private notification", "Private habit", null, null, false, DateTime.UnixEpoch)],
                1, 11)));

        var context = new DefaultHttpContext();
        context.Request.Path = route switch
        {
            "chat" => "/api/chat",
            "records" => "/api/chat/records/notifications",
            _ => $"/api/chat/{route}"
        };
        context.Response.Body = new MemoryStream();
        if (credential == "jwt")
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, UserId.ToString())], "Bearer"));
        }
        else
        {
            var connector = credential.StartsWith("oauth:");
            if (connector) credential = credential["oauth:".Length..];
            var scopes = credential == "" ? Array.Empty<string>()
                : new[] { credential == "read_only_chat" ? AgentScopes.ChatInteract : credential };
            var (key, rawKey) = ApiKey.Create(UserId, "Chat key", scopes,
                isReadOnly: credential is "read_only_chat" or AgentScopes.ReadHabits,
                oauthFamilyId: connector ? Guid.NewGuid() : null).Value;
            var repository = Substitute.For<IGenericRepository<ApiKey>>();
            repository.FindTrackedAsync(Arg.Any<Expression<Func<ApiKey, bool>>>(), Arg.Any<CancellationToken>())
                .Returns([key]);
            var payGate = Substitute.For<IPayGateService>();
            payGate.CanReadApiKeys(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
            using var services = new ServiceCollection()
                .AddSingleton(Substitute.For<IApiKeyUsageTracker>())
                .AddSingleton(repository)
                .AddSingleton(payGate)
                .AddSingleton(Substitute.For<IUnitOfWork>())
                .AddMemoryCache()
                .BuildServiceProvider();
            var options = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
            options.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
            var authentication = new ApiKeyAuthenticationHandler(options, NullLoggerFactory.Instance, UrlEncoder.Default, services);
            context.Request.Headers.Authorization = $"Bearer {rawKey}";
            await authentication.InitializeAsync(new AuthenticationScheme("ApiKey", "ApiKey", typeof(ApiKeyAuthenticationHandler)), context);
            var authenticated = await authentication.AuthenticateAsync();
            authenticated.Succeeded.Should().BeTrue();
            context.User = authenticated.Principal!;
        }

        return new ChatController(_mediator, _imageValidation, NullLogger<ChatController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static Task<IActionResult> InvokeAsync(ChatController controller, string route) => route switch
    {
        "chat" => controller.ProcessChat("Hello", null, null, CancellationToken.None),
        "stream" => controller.ProcessChatStream("Hello", null, null, CancellationToken.None),
        "records" => controller.GetRecordListPage("notifications",
            RecordListCursor.Create(UserId, "notifications", RecordListCardBuilder.PageSize), CancellationToken.None),
        "transcribe" => controller.Transcribe(new FormFile(new MemoryStream([1, 2, 3]), 0, 3, "audio", "clip.webm"), CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(route))
    };
}
