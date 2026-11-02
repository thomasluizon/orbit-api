using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Chat.Tools;

public class ManageApiKeysToolTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ManageApiKeysTool _tool;
    private static readonly Guid UserId = Guid.NewGuid();

    public ManageApiKeysToolTests()
    {
        _tool = new ManageApiKeysTool(_mediator);
    }

    [Theory]
    [InlineData("not-a-timestamp")]
    [InlineData("2026-04-20")]
    public async Task ExecuteAsync_CreateWithInvalidExpiry_ReturnsValidationError(string expiry)
    {
        var result = await Execute($$"""{"action":"create","name":"Claude","expires_at_utc":"{{expiry}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("expires_at_utc must be a valid ISO-8601 UTC timestamp.");
        await _mediator.DidNotReceiveWithAnyArgs().Send(default!, default);
    }

    [Theory]
    [InlineData("2026-04-20T18:00:00Z")]
    [InlineData("2026-04-20T15:00:00-03:00")]
    public async Task ExecuteAsync_CreateWithValidExpiry_PassesParsedUtcTimestampToCommand(string expiry)
    {
        var expectedExpiry = DateTime.Parse(
            "2026-04-20T18:00:00Z",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        _mediator.Send(Arg.Any<CreateApiKeyCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new CreateApiKeyResponse(
                Guid.NewGuid(),
                "Claude",
                "orb_12345678901234567890123456789012",
                "orb_12345678",
                [],
                false,
                expectedExpiry,
                DateTime.UtcNow)));

        var result = await Execute($$"""{"action":"create","name":"Claude","expires_at_utc":"{{expiry}}"}""");

        result.Success.Should().BeTrue();
        await _mediator.Received(1).Send(
            Arg.Is<CreateApiKeyCommand>(command => command.ExpiresAtUtc == expectedExpiry),
            Arg.Any<CancellationToken>());
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
