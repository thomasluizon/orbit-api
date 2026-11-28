using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.Extensions;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Services;

namespace Orbit.Infrastructure.Tests.Controllers;

public sealed class ChatBlockCloseTests : IDisposable
{
    private readonly ConversationTurnRecorderTests.Fixture _fixture = new();
    private readonly IPayGateService _allowance = Substitute.For<IPayGateService>();
    private readonly IAiIntentService _model = Substitute.For<IAiIntentService>();
    private readonly IssuedAiBlockStore _store;
    private readonly UnitOfWork _work;
    private readonly ServiceProvider _services;

    public ChatBlockCloseTests()
    {
        _store = new(_fixture.Db);
        _work = new(_fixture.Db, new DatabaseConnectionSettings());
        _services = new ServiceCollection().AddLogging()
            .AddMediatR(config => config.RegisterServicesFromAssemblyContaining<CloseChatBlockCommand>())
            .AddSingleton<IIssuedAiBlockStore>(_store).AddSingleton<IUnitOfWork>(_work)
            .AddSingleton<IConversationTurnRecorder>(_fixture.Store).AddSingleton(_fixture.Flags)
            .AddSingleton(_allowance).AddSingleton(_model).BuildServiceProvider();
    }

    private Guid UserId => _fixture.User.Id;

    [Fact]
    public async Task Close_OpenBlock_Returns204TwiceAndKeepsTheFirstInstantWithoutModelOrAllowance()
    {
        var block = await IssueAsync();
        var before = DateTime.UtcNow;
        (await CloseAsync(block.Id)).Should().BeOfType<NoContentResult>();
        var first = (await ReadAsync(block.Id)).InvalidatedAtUtc;
        first.Should().NotBeNull().And.BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
        (await CloseAsync(block.Id)).Should().BeOfType<NoContentResult>();
        (await ReadAsync(block.Id)).InvalidatedAtUtc.Should().Be(first);
        _allowance.ReceivedCalls().Should().BeEmpty();
        _model.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Close_ForeignOrUnknownBlock_Returns404AndKeepsTheBlockOpen(bool foreign)
    {
        var block = await IssueAsync();
        var result = foreign ? await CloseAsync(block.Id, Guid.NewGuid()) : await CloseAsync(Guid.NewGuid());
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
        (await ReadAsync(block.Id)).InvalidatedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Close_AnsweredBlock_Returns409WithTheProblemOfTheAnswerPath()
    {
        var block = await IssueAsync();
        (await _store.MarkAnsweredAsync(block.Id, UserId, DateTime.UtcNow, default)).Should().BeTrue();
        var answer = await AnswerAsync(block.Id);
        var result = (await CloseAsync(block.Id)).Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(409);
        result.Value.Should().Be(answer.ToErrorResult().Should().BeOfType<ObjectResult>().Which.Value);
        (await ReadAsync(block.Id)).InvalidatedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Answer_AfterClose_IsRefusedLikeAnAnswerAfterAnInvalidation()
    {
        var closed = await IssueAsync();
        await CloseAsync(closed.Id);
        var afterClose = await AnswerAsync(closed.Id);
        var invalidated = await IssueAsync();
        await _store.InvalidateOpenAsync(UserId, default);
        var afterInvalidation = await AnswerAsync(invalidated.Id);
        afterClose.ErrorCode.Should().Be(ErrorCodes.AiBlockInvalidated).And.Be(afterInvalidation.ErrorCode);
        (await _store.MarkAnsweredAsync(closed.Id, UserId, DateTime.UtcNow, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Close_ReadOnlyCredential_IsForbidden()
    {
        var block = await IssueAsync();
        var result = await Controller(UserId, new Claim("api_key_read_only", "true")).CloseBlock(block.Id, default);
        result.Should().BeOfType<ForbidResult>();
        (await ReadAsync(block.Id)).InvalidatedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task History_AddsClosedAtUtcOnlyToAClosedBlockWithoutAnAnswer()
    {
        var byMessage = await IssueAsync();
        var answered = await IssueAsync();
        await _store.MarkAnsweredAsync(answered.Id, UserId, DateTime.UtcNow, default);
        await _store.InvalidateOpenAsync(UserId, default);
        var byClose = await IssueAsync();
        await CloseAsync(byClose.Id);
        var open = await IssueAsync();
        (string Kind, IssuedAiBlock Block, bool Closed)[] cases =
            [("question", byClose, true), ("choice", byMessage, true), ("pickList", byMessage, true),
                ("question", open, false), ("choice", answered, false)];
        var response = JsonSerializer.Serialize(new
        {
            aiMessage = "Reply",
            parts = cases.Select(item => new { type = "block", kind = item.Kind, block = new { blockId = item.Block.Id } })
        }, JsonSerializerOptions.Web);
        (await _fixture.Store.RecordAsync(_fixture.Capture() with { ResponseJson = response })).Should().BeTrue();

        var page = (await Controller(UserId).GetLatestConversation(default)).Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<ConversationPage>().Subject;

        var history = JsonNode.Parse(page.Turns.Single(turn => turn.Role == "assistant").ResponseJson!)!;
        foreach (var (item, index) in cases.Select((item, index) => (item, index)))
        {
            var block = history["parts"]![index]!["block"]!.AsObject();
            block.ContainsKey("closedAtUtc").Should().Be(item.Closed);
            if (!item.Closed) continue;
            var closedAt = block["closedAtUtc"]!.GetValue<string>();
            closedAt.Should().EndWith("Z");
            DateTimeOffset.Parse(closedAt, CultureInfo.InvariantCulture).UtcDateTime
                .Should().Be((await ReadAsync(item.Block.Id)).InvalidatedAtUtc);
            block.Remove("closedAtUtc");
        }
        JsonNode.DeepEquals(history, JsonNode.Parse(response)).Should().BeTrue("the history only appends a field");
    }

    private async Task<IssuedAiBlock> IssueAsync()
    {
        var block = IssuedAiBlock.Create(UserId,
            new AiBlockDefinition("choice", "When?", [new("a", "Morning"), new("b", "Evening")], []));
        await _store.AddAsync(block, default);
        return block;
    }

    private async Task<IssuedAiBlock> ReadAsync(Guid blockId) => (await _store.GetAsync(blockId, UserId, default))!;

    private Task<IActionResult> CloseAsync(Guid blockId, Guid? userId = null) =>
        Controller(userId ?? UserId).CloseBlock(blockId, default);

    private Task<Result<PreparedAiBlockAnswer>> AnswerAsync(Guid blockId) =>
        new AiBlockAnswerService(_store, _work, _allowance, Substitute.For<IAgentOperationExecutor>(),
                Substitute.For<IGenericRepository<User>>(), Substitute.For<IProductAnalytics>(), _fixture.Flags,
                Substitute.For<IPendingAgentOperationStore>(), Substitute.For<IUserDateService>())
            .PrepareAsync(new(UserId, "Morning", BlockAnswer: new(blockId, ["a"])), default);

    private ChatController Controller(Guid userId, params Claim[] claims) =>
        new(_services.GetRequiredService<IMediator>(), Substitute.For<IImageValidationService>(), NullLogger<ChatController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), .. claims], "Test"))
                }
            }
        };

    public void Dispose()
    {
        _services.Dispose();
        _fixture.Dispose();
    }
}
