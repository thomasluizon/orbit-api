using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class ChatControllerTests
{
    [Theory]
    [InlineData("en", false, "I can't help")]
    [InlineData("pt-BR", false, "Não posso ajudar")]
    [InlineData("en", true, "I can't help")]
    [InlineData("pt-BR", true, "Não posso ajudar")]
    public async Task ProcessChat_FlaggedInput_RealHandlerReturnsOk(string language, bool parts, string decline)
    {
        var user = User.Create("Person", "moderation@example.com").Value;
        user.SetLanguage(language);
        var users = Substitute.For<IGenericRepository<User>>();
        users.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        var moderation = Substitute.For<IContentModerationService>();
        moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationResult(true, false, []));
        var handler = new ProcessUserChatCommandHandler(
            new ChatDataDependencies(Substitute.For<IGenericRepository<Habit>>(), Substitute.For<IGenericRepository<Goal>>(),
                users, Substitute.For<IGenericRepository<Tag>>(), Substitute.For<IGenericRepository<ChecklistTemplate>>(),
                Substitute.For<IFeatureFlagService>()),
            new ChatAiDependencies(Substitute.For<IAiIntentService>(), new AiToolRegistry([]),
                Substitute.For<ISystemPromptBuilder>(), Substitute.For<IAgentCatalogService>()),
            new ChatExecutionDependencies(Substitute.For<IUserDateService>(), Substitute.For<IUserStreakService>(),
                Substitute.For<IPayGateService>(), Substitute.For<IUnitOfWork>(), Substitute.For<IServiceScopeFactory>(),
                Substitute.For<IAgentOperationExecutor>(), Substitute.For<IPendingClarificationStore>(),
                Substitute.For<IGoalProgressReadSyncer>(), Substitute.For<IGamificationService>(), _mediator,
                Substitute.For<IProductAnalytics>(), Substitute.For<IHabitScheduleLogReader>(),
                Substitute.For<IIssuedAstraBlockStore>(), Substitute.For<IPendingAgentOperationStore>()),
            NullLogger<ProcessUserChatCommandHandler>.Instance, Substitute.For<IAppConfigService>(), moderation);
        _mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<ProcessUserChatCommand>(), call.Arg<CancellationToken>()));

        var result = await _controller.ProcessChat("screen this input", null, null, default,
            clientContext: parts ? "{\"supportsParts\":true}" : null);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(200);
        var response = ok.Value.Should().BeOfType<ChatResponse>().Subject;
        response.AiMessage.Should().StartWith(decline);
        response.Actions.Should().BeEmpty();
        if (parts)
            response.Parts.Should().Equal(new ChatTextPart(response.AiMessage!));
        else
            response.Parts.Should().BeNull();
    }
}
