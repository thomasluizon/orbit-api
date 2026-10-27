using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Subscriptions.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", "free")]
    [InlineData("pt-BR", "free")]
    [InlineData("en", "play")]
    [InlineData("pt-BR", "play")]
    [InlineData("en", "missing")]
    [InlineData("pt-BR", "missing")]
    public async Task EmptySubscriptionRead_PreservesSuccessfulAccountBlock(string language, string subscription)
    {
        var user = User.Create("Person", "person@example.com").Value;
        user.SetLanguage(language);
        if (subscription is "play" or "missing")
        {
            var expiresAtUtc = DateTime.UtcNow.AddDays(30);
            user.SetStripeCustomerId("cus_old");
            user.SetStripeSubscription("sub_old", expiresAtUtc);
            if (subscription == "play") user.SetPlaySubscription("play_token", expiresAtUtc);
        }
        SetupUserAndPayGate(user);
        EnableComposition();
        var billing = Substitute.For<IBillingService>();
        billing.GetSubscriptionDetailsAsync("sub_old", Arg.Any<CancellationToken>())
            .Returns((BillingSubscriptionDetails?)null);
        var billingHandler = new GetBillingDetailsQueryHandler(_userRepo, billing,
            Substitute.For<ILogger<GetBillingDetailsQueryHandler>>());
        var statusHandler = new GetSubscriptionStatusQueryHandler(_userRepo, _payGate, _userDateService);
        _mediator.Send(Arg.Any<GetBillingDetailsQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => billingHandler.Handle(call.Arg<GetBillingDetailsQuery>(), call.Arg<CancellationToken>()));
        _mediator.Send(Arg.Any<GetSubscriptionStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => statusHandler.Handle(call.Arg<GetSubscriptionStatusQuery>(), call.Arg<CancellationToken>()));
        var tool = new GetSubscriptionOverviewTool(_mediator);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(tool.Name, "plan", """{"include_plans":false}"""));
        ContinueWithReadDirective("[[orbit:read:plan]]");

        var result = await handler.Handle(new(UserId,
            language == "en" ? "Show my subscription" : "Mostre minha assinatura",
            ClientContext: new(SupportsParts: true, PartKinds: ["accountRows"], SupportsAccountRowsCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        var operation = result.Value.Operations.Should().ContainSingle().Subject;
        operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        var payload = operation.Payload.Should().BeOfType<SubscriptionOverviewPayload>().Subject;
        payload.Status.Should().NotBeNull();
        payload.Billing.Should().BeNull();
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Kind.Should().Be("accountRows");
        result.Value.AiMessage.Should().BeNullOrEmpty();
        if (subscription != "missing") billing.ReceivedCalls().Should().BeEmpty();

        var stored = JsonSerializer.Deserialize<ChatResponse>(JsonSerializer.Serialize(result.Value,
            ChatStreamEvent.SerializerOptions), ChatStreamEvent.SerializerOptions)!;
        var fallback = ChatResponseNegotiation.ForClient(stored, null, [], language, user.TimeZone);
        fallback.AiMessage.Should().Contain(language == "en" ? "Plan:" : "Plano:")
            .And.NotContain(EnglishToolFailureMessage).And.NotContain(PortugueseToolFailureMessage);
    }
}
