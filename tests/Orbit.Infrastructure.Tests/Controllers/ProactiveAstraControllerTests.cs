using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Common;
using Orbit.Application.Proactive.Commands;
using Orbit.Application.Proactive.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Tests.Services;

namespace Orbit.Infrastructure.Tests.Controllers;

public sealed class ProactiveAstraControllerTests
{
    private readonly ISender _sender = Substitute.For<ISender>();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly ProactiveAstraController _controller;

    public ProactiveAstraControllerTests()
    {
        _controller = new(_sender)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, _userId.ToString())], "Test"))
                }
            }
        };
    }

    [Fact]
    public void Status_RequiresAuthentication()
    {
        typeof(ProactiveAstraController).Should()
            .BeDecoratedWith<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>();
    }

    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v2.json")]
    [InlineData("openapi_v1-r2.json")]
    [InlineData("openapi_v2-r2.json")]
    public void Current_PublishedContractAppendsOptionalChatPartsAndQueryNegotiation(string name)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", "src/Orbit.Api", name));
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var schema = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("ProactiveItemResponse");
        var properties = schema.GetProperty("properties");
        properties.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "id", "trigger", "localDate", "text", "actionLabel", "actionPrompt", "createdAtUtc", "shownAtUtc", "pushedAtUtc", "facts", "parts");
        var parts = properties.GetProperty("parts");
        parts.GetProperty("type").EnumerateArray().Select(type => type.GetString()).Should().BeEquivalentTo("array", "null");
        parts.GetProperty("items").GetProperty("$ref").GetString().Should().Be("#/components/schemas/ChatResponsePart");
        if (schema.TryGetProperty("required", out var required))
            required.EnumerateArray().Select(value => value.GetString()).Should().NotContain("parts");
        var operation = document.RootElement.GetProperty("paths").GetProperty("/api/ai/proactive/current").GetProperty("get");
        var context = operation.GetProperty("parameters").EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "clientContext");
        context.GetProperty("in").GetString().Should().Be("query");
        context.TryGetProperty("required", out var mandatory).Should().BeFalse();
        operation.GetProperty("responses").TryGetProperty("204", out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("active")]
    [InlineData("ended")]
    public async Task Status_ReturnsOnlyIdAndLifecycleForAuthenticatedOwner(string status)
    {
        var id = Guid.NewGuid();
        var response = new ProactiveItemStatusResponse(id, status);
        using var cancellation = new CancellationTokenSource();
        _sender.Send(Arg.Any<GetProactiveItemStatusQuery>(), cancellation.Token)
            .Returns(Result.Success<ProactiveItemStatusResponse?>(response));

        (await _controller.GetById(id, cancellation.Token)).Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().Be(response);
        await _sender.Received(1).Send(
            Arg.Is<GetProactiveItemStatusQuery>(q => q.UserId == _userId && q.ItemId == id), cancellation.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Status_MissingAndForeignItemsAre404WithoutContent(bool foreignItem)
    {
        using var h = new ProactiveEngineServiceTests.Harness(19);
        h.SeedTrigger(ProactiveTrigger.EveningCheckin);
        await h.Run();
        var id = foreignItem ? h.Db.ProactiveItems.Single().Id : Guid.NewGuid();
        var handler = new GetProactiveItemStatusQueryHandler(h.Engine);
        _sender.Send(Arg.Any<GetProactiveItemStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<GetProactiveItemStatusQuery>(), call.Arg<CancellationToken>()));

        var result = await _controller.GetById(id, default);

        result.Should().NotBeAssignableTo<IClientErrorActionResult>();
        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(404);
        content.Content.Should().BeNullOrEmpty();
        await _sender.Received(1).Send(
            Arg.Is<GetProactiveItemStatusQuery>(q => q.UserId == _userId && q.ItemId == id), Arg.Any<CancellationToken>());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        await using var provider = services.BuildServiceProvider();
        _controller.HttpContext.RequestServices = provider;
        _controller.HttpContext.Response.Body = new MemoryStream();
        await result.ExecuteResultAsync(_controller.ControllerContext);
        _controller.Response.StatusCode.Should().Be(404);
        _controller.Response.Body.Length.Should().Be(0);
    }

    [Fact]
    public async Task Current_EmptyIs204AndUsesAuthenticatedOwner()
    {
        _sender.Send(Arg.Any<GetCurrentProactiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<ProactiveItemResponse?>(null));
        (await _controller.Current(default)).Should().BeOfType<NoContentResult>();
        await _sender.Received(1).Send(Arg.Is<GetCurrentProactiveQuery>(q => q.UserId == _userId), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task Current_ForwardsNegotiatedContextAndCredentialRestrictions(string locale)
    {
        _controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, _userId.ToString()), new Claim("auth_method", "api_key"),
                new Claim("scope", "habits:read"), new Claim("api_key_read_only", "true")], "Test"));
        _sender.Send(Arg.Any<GetCurrentProactiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<ProactiveItemResponse?>(null));
        var context = System.Text.Json.JsonSerializer.Serialize(new
        {
            locale,
            supportsParts = true,
            partKinds = new[] { "habitList" },
            supportsHabitListCard = true
        });

        (await _controller.Current(default, context)).Should().BeOfType<NoContentResult>();

        await _sender.Received(1).Send(Arg.Is<GetCurrentProactiveQuery>(q => q.UserId == _userId
            && q.ClientContext != null && q.ClientContext.Locale == locale && q.ClientContext.SupportsParts == true
            && q.ClientContext.SupportsHabitListCard == true && q.ClientContext.PartKinds!.SequenceEqual(new[] { "habitList" })
            && q.AuthMethod == AgentAuthMethod.ApiKey && q.GrantedScopes!.SequenceEqual(new[] { "habits:read" })
            && q.IsReadOnlyCredential), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("invalid")]
    [InlineData("[]")]
    [InlineData("{\"supportsParts\":\"yes\"}")]
    public async Task Current_InvalidContextIs400WithoutDispatch(string context)
    {
        (await _controller.Current(default, context)).Should().BeOfType<BadRequestObjectResult>();
        _sender.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outcome_MissingItemIs404AndUsesAuthenticatedOwner(bool acted)
    {
        var id = Guid.NewGuid();
        _sender.Send(Arg.Any<RecordProactiveOutcomeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorMessages.ProactiveItemNotFound));
        var result = acted ? await _controller.Acted(id, default) : await _controller.Opened(id, default);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
        await _sender.Received(1).Send(Arg.Is<RecordProactiveOutcomeCommand>(q => q.UserId == _userId && q.ItemId == id && q.Acted == acted), Arg.Any<CancellationToken>());
    }
}
