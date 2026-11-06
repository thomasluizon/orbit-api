using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", "Add a nightly magnesium habit.", "Post Lunch", "Before Bed")]
    [InlineData("pt-BR", "Adicione um hábito noturno de tomar magnésio.", "Depois do almoço", "Antes de dormir")]
    public async Task SuggestionCandidates_NameTheFittingOwnedParent(string locale, string message, string lunch, string bed)
    {
        var (lunchParent, bedParent) = SetupCandidateParents(lunch, bed);

        var candidates = await CandidatesSentAsync(locale, message);

        candidates.GetProperty("ParentHabitIds").EnumerateArray().Select(id => id.GetGuid()).Should().Equal(bedParent.Id);
        candidates.GetProperty("ParentHabitIds").EnumerateArray().Should().NotContain(id => id.GetGuid() == lunchParent.Id);
        candidates.GetProperty("SupportedDefault").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("en", "Create a nightly journaling habit at the top level, without a parent.")]
    [InlineData("pt-BR", "Crie um hábito noturno de escrever no diário no nível principal, sem pai.")]
    public async Task SuggestionCandidates_RespectAnExplicitRootPlacement(string locale, string message)
    {
        SetupCandidateParents("Post Lunch", "Before Bed");

        var candidates = await CandidatesSentAsync(locale, message);

        candidates.GetProperty("ParentHabitIds").GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("en", "Create a daily ten-minute mobility session.", "Daily mobility")]
    [InlineData("pt-BR", "Crie uma sessão diária de mobilidade de dez minutos.", "Mobilidade diária")]
    public async Task SuggestionCandidates_NameTheFittingActiveGoal(string locale, string message, string title)
    {
        var goal = Goal.Create(UserId, title, targetValue: 10, unit: "sessions").Value;
        var other = Goal.Create(UserId, "Read twelve books", targetValue: 12, unit: "books").Value;
        _goalRepo.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<CancellationToken>()).Returns([goal, other]);
        _goalRepo.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>>(), Arg.Any<CancellationToken>()).Returns([goal, other]);

        var candidates = await CandidatesSentAsync(locale, message);

        candidates.GetProperty("GoalIds").EnumerateArray().Select(id => id.GetGuid()).Should().Equal(goal.Id);
    }

    [Theory]
    [InlineData("en", "Help me remember to drink water throughout the day.", false)]
    [InlineData("pt-BR", "Me ajude a lembrar de beber água ao longo do dia.", false)]
    [InlineData("en", "Help me set up a new routine.", true)]
    public async Task SuggestionCandidates_KeepAskOutOfACreateTurnWithASupportedDefault(string locale, string message, bool askOffered)
    {
        var request = await SentRequestAsync(locale, message);

        var tools = request.ToolDeclarations.Select(value => JsonSerializer.SerializeToElement(value).GetProperty("name").GetString());
        if (askOffered) tools.Should().Contain("ask");
        else tools.Should().NotContain("ask");
    }

    [Theory]
    [InlineData("en", "Show my Before Bed habits.")]
    [InlineData("pt-BR", "Mostre meus hábitos de Antes de dormir.")]
    [InlineData("en", "Rename my Before Bed routine to Night wind-down.")]
    [InlineData("pt-BR", "Renomeie minha rotina Antes de dormir para Fim do dia.")]
    [InlineData("en", "Skip my reminders all day today.")]
    public async Task SuggestionCandidates_KeepAskInATurnThatCreatesNothing(string locale, string message)
    {
        SetupCandidateParents("Post Lunch", "Before Bed");

        var request = await SentRequestAsync(locale, message);

        request.ToolDeclarations.Select(value => JsonSerializer.SerializeToElement(value).GetProperty("name").GetString())
            .Should().Contain("ask");
        var candidates = await CandidatesSentAsync(locale, message);
        candidates.GetProperty("SupportedDefault").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("en", "Add a nightly magnesium habit.")]
    [InlineData("pt-BR", "Adicione um hábito noturno de tomar magnésio.")]
    public async Task SuggestionCandidates_KeepAskOutOfACreateTurnWithAFittingParent(string locale, string message)
    {
        SetupCandidateParents("Post Lunch", "Before Bed");

        var request = await SentRequestAsync(locale, message);

        request.ToolDeclarations.Select(value => JsonSerializer.SerializeToElement(value).GetProperty("name").GetString())
            .Should().NotContain("ask");
    }

    private (Habit Lunch, Habit Bed) SetupCandidateParents(string lunch, string bed)
    {
        var parents = new[] { lunch, bed }.Select(title => Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            UserId, title, FrequencyUnit.Day, 1, Today)).Value).ToArray();
        var children = parents.Select(parent => Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            UserId, "Existing step", FrequencyUnit.Day, 1, Today, ParentHabitId: parent.Id)).Value);
        SetupOfferHabits([.. parents, .. children]);
        return (parents[0], parents[1]);
    }

    private async Task<JsonElement> CandidatesSentAsync(string locale, string message)
    {
        var request = await SentRequestAsync(locale, message);
        var line = request.DynamicContext!.Split(Environment.NewLine)
            .Should().ContainSingle(entry => entry.StartsWith("{\"offer_suggestion_candidates\"", StringComparison.Ordinal)).Which;
        return JsonDocument.Parse(line).RootElement.GetProperty("offer_suggestion_candidates").Clone();
    }

    private async Task<AiToolRequest> SentRequestAsync(string locale, string message)
    {
        SetupUserAndPayGate();
        AiToolRequest? sent = null;
        _aiIntentService.SendWithToolsAsync(Arg.Do<AiToolRequest>(request => sent ??= request),
                Arg.Any<Func<AiStreamEvent, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Done" }));
        var client = OfferClient(locale) with { PartKinds = ["habitList", "goalList", "choice"] };

        await CreateHandler(RealAskTool()).Handle(new(UserId, message, ClientContext: client), default);

        return sent!;
    }
}
