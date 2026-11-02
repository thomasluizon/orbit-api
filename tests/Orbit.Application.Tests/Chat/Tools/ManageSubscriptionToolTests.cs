using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Application.Subscriptions;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Chat.Tools;

public class ManageSubscriptionToolTests
{
    [Fact]
    public async Task ExecuteAsync_CheckoutAndPortal_ForwardEachCallerAndRequestedInterval()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CreateCheckoutCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new CheckoutResponse("https://checkout")));
        mediator.Send(Arg.Any<CreatePortalSessionCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new PortalResponse("https://portal")));
        var tool = new ManageSubscriptionTool(mediator);
        foreach (var caller in new[] { Guid.NewGuid(), Guid.NewGuid() })
        {
            (await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { action = "create_checkout", interval = "yearly" }), caller, default)).Success.Should().BeTrue();
            (await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { action = "create_portal" }), caller, default)).Success.Should().BeTrue();
            await mediator.Received(1).Send(Arg.Is<CreateCheckoutCommand>(c => c.UserId == caller && c.Interval == "yearly"), Arg.Any<CancellationToken>());
            await mediator.Received(1).Send(Arg.Is<CreatePortalSessionCommand>(c => c.UserId == caller), Arg.Any<CancellationToken>());
        }
    }
}
