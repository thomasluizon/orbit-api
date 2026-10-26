using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class ChatControllerTests
{
    private static HabitSuggestionDraft SuggestionDraft() => new(Guid.NewGuid(), "📖", "Read", "Habit", FrequencyUnit.Day,
        1, [], new(2026, 10, 26), new(22, 0), new(22, 15), null, [], "A quiet pause before bed.");

    [Fact]
    public async Task HabitSuggestionRefine_ReturnsOnlyDraftAndUsesAuthenticatedOwner()
    {
        var draft = SuggestionDraft();
        var refined = draft with { DueTime = new(22, 30), DueEndTime = new(22, 45) };
        _mediator.Send(Arg.Any<RefineHabitSuggestionQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success(refined));
        var result = await _controller.RefineHabitSuggestion(new(draft, "weekdays at 22:30", "en"), default);
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(refined);
        await _mediator.Received(1).Send(Arg.Is<RefineHabitSuggestionQuery>(query => query.UserId == UserId
            && query.Draft == draft && query.Instruction == "weekdays at 22:30"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("api_key", false)]
    [InlineData("api_key", true)]
    [InlineData("oauth", false)]
    [InlineData("oauth", true)]
    public async Task HabitSuggestions_ScopedCredentialsRequireHabitsAndGoals(string authMethod, bool goalsScope)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, UserId.ToString()), new("auth_method", authMethod),
            new("scope", AgentScopes.ChatInteract), new("scope", AgentScopes.ReadHabits)];
        if (goalsScope) claims.Add(new("scope", AgentScopes.ReadGoals));
        _controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        var draft = SuggestionDraft();
        _mediator.Send(Arg.Any<RefineHabitSuggestionQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success(draft));
        _mediator.Send(Arg.Any<PreviewHabitSuggestionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AgentExecuteOperationResponse>("Invalid draft."));

        var refine = await _controller.RefineHabitSuggestion(new(draft, "make it easier"), default);
        var preview = await _controller.PreviewHabitSuggestion(new(draft), default);

        if (goalsScope)
        {
            refine.Should().BeOfType<OkObjectResult>();
            preview.Should().NotBeOfType<ForbidResult>();
            await _mediator.Received(1).Send(Arg.Is<PreviewHabitSuggestionCommand>(command => command.UserId == UserId
                && command.GrantedScopes != null && command.GrantedScopes.Contains(AgentScopes.ReadGoals)), Arg.Any<CancellationToken>());
        }
        else
        {
            refine.Should().BeOfType<ForbidResult>();
            preview.Should().BeOfType<ForbidResult>();
            _mediator.ReceivedCalls().Should().BeEmpty();
        }
    }

    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v1-r2.json")]
    [InlineData("openapi_v2.json")]
    [InlineData("openapi_v2-r2.json")]
    public void HabitSuggestionContract_IsPublishedInEverySupportedStream(string artifact)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", "src/Orbit.Api"));
        var json = File.ReadAllText(Path.Combine(root, artifact));
        json.Should().Contain("/api/Chat/habit-suggestions/refine").And.Contain("/api/Chat/habit-suggestions/preview");
        json.Should().Contain("/api/Chat/habit-suggestions/preview-all").And.Contain("PreviewAllHabitSuggestionsRequest").And.Contain("draftIndex");
        json.Should().Contain("HabitSuggestionDraft").And.Contain("existingHabitId");
    }
}
