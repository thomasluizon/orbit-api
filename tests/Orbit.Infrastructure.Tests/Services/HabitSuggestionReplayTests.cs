using System.ClientModel.Primitives;
using System.Linq.Expressions;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.AI;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class HabitSuggestionReplayTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly DateOnly _today = new(2026, 10, 27);

    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", true)]
    public async Task OmittedReminder_UsesCorrectionRatherThanDefaultingToDisabled(string language, bool refine)
    {
        var valid = TimedModelResponse(language, refine);
        using var replay = new ReplayHandler(valid.Replace("\"reminderMinutesBefore\":null,", ""), valid);

        var result = await Run(replay, language, refine);

        result.IsSuccess.Should().BeTrue(result.Error);
        replay.RequestCount.Should().Be(2);
        replay.CorrectionAt(1).Should().Contain("reminderMinutesBefore");
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    public async Task NullTimes_UseCorrectionAndReturnTimedCards(string language, bool refine)
    {
        using var replay = new ReplayHandler(ModelResponse(language, refine), TimedModelResponse(language, refine));
        var result = await Run(replay, language, refine);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Drafts.Should().HaveCount(refine ? 1 : 3);
        result.Value.Language.Should().Be(language);
        var draft = result.Value.Drafts[0];
        draft.DueTime.Should().Be(new TimeOnly(21, 30));
        draft.DueEndTime.Should().Be(new TimeOnly(21, 45));
        draft.ToHabit(_userId).IsSuccess.Should().BeTrue();
        if (refine) draft.Id.Should().Be(Original().Id);
        replay.RequestCount.Should().Be(2);
        replay.CorrectionAt(0).Should().BeNull();
        replay.CorrectionAt(1).Should().Contain("dueTime").And.Contain("dueEndTime")
            .And.Contain("non-null HH:mm").And.Contain("end after start");
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    public async Task MalformedDraft_UsesCorrectionAndReturnsCards(string language, bool refine)
    {
        var valid = TimedModelResponse(language, refine);
        using var replay = new ReplayHandler(ModelResponse(language, refine).Replace("\"dueTime\": null", "\"dueTime\": \"not-a-time\""), valid);
        var result = await Run(replay, language, refine);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Drafts.Should().HaveCount(refine ? 1 : 3);
        replay.RequestCount.Should().Be(2);
        replay.CorrectionAt(1).Should().Contain("non-null HH:mm");
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    public async Task RepeatedNullTimes_ReturnUnavailableAfterOneCorrection(string language, bool refine)
    {
        using var replay = new ReplayHandler(ModelResponse(language, refine));
        var result = await Run(replay, language, refine);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("HABIT_SUGGESTION_UNAVAILABLE");
        replay.RequestCount.Should().Be(2);
        replay.CorrectionAt(1).Should().Contain("non-null HH:mm");
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", true)]
    public async Task InvalidTimeRange_UsesValidationCorrectionAndReturnsCards(string language, bool refine)
    {
        var valid = TimedModelResponse(language, refine);
        using var replay = new ReplayHandler(valid.Replace("21:45", "21:15"), valid);

        var result = await Run(replay, language, refine);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Drafts.Should().HaveCount(refine ? 1 : 3);
        replay.RequestCount.Should().Be(2);
        replay.CorrectionAt(1).Should().NotBeNullOrWhiteSpace();
    }

    private async Task<Result<HabitSuggestionsCard>> Run(ReplayHandler replay, string language, bool refine)
    {
        using var http = new HttpClient(replay);
        var completion = new AiCompletionClient(Options.Create(new AiSettings
        { ApiKey = "test-key", BaseUrl = "https://orbit.test/v1" }),
            NullLogger<AiCompletionClient>.Instance, Substitute.For<IAiUsageRecorder>(), new HttpClientPipelineTransport(http));
        var generator = new AiHabitDraftGenerator(AiBudgetTestDouble.Wrap(completion), NullLogger<AiHabitDraftGenerator>.Instance);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(_today);
        dates.GetUserWeekStartDayAsync(_userId, Arg.Any<CancellationToken>()).Returns(1);
        var habits = Substitute.For<IGenericRepository<Habit>>();
        habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Habit>());
        var goals = Substitute.For<IGenericRepository<Goal>>();
        goals.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Goal>());
        var service = new HabitSuggestionService(habits, goals, dates, generator);
        if (!refine)
            return await new SuggestHabitDraftsQueryHandler(service).Handle(new(_userId,
                language == "en" ? "Suggest 3 habits for better sleep" : "Sugira 3 hábitos para dormir melhor", language), default);

        var gate = Substitute.For<IPayGateService>();
        gate.TryConsumeAiMessage(_userId, Arg.Any<IUnitOfWork>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var moderation = Substitute.For<IContentModerationService>();
        moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ModerationResult(false, false, []));
        var result = await new RefineHabitSuggestionQueryHandler(service, gate, Substitute.For<IUnitOfWork>(), moderation)
            .Handle(new(_userId, Original(), language == "en" ? "Move it earlier" : "Mova para mais cedo", language), default);
        return result.IsSuccess
            ? Result.Success(new HabitSuggestionsCard(language, [result.Value]))
            : Result.Failure<HabitSuggestionsCard>(result.Error, result.ErrorCode);
    }

    private HabitSuggestionDraft Original() => new(Guid.Parse("164940d3-f222-4270-861f-53ddc586bf24"), "📖", "Read", "Habit",
        FrequencyUnit.Day, 1, [], _today, new(22, 0), new(22, 15), null, [], "A quiet pause before bed.");

    private static string TimedModelResponse(string language, bool refine) => ModelResponse(language, refine)
        .Replace("\"dueTime\": null", "\"dueTime\":\"21:30\"")
        .Replace("\"dueEndTime\":null", "\"dueEndTime\":\"21:45\"");

    private static string ModelResponse(string language, bool refine)
    {
        var title = language == "en" ? "Read" : "Ler";
        var reason = language == "en" ? "A quiet pause before bed." : "Uma pausa tranquila antes de dormir.";
        var first = $$"""
            {"id":"164940d3-f222-4270-861f-53ddc586bf24","emoji":"📖","title":"{{title}}","type":"Habit",
             "frequencyUnit":"Day","frequencyQuantity":1,"days":[],"dueDate":"2026-10-27",
             "dueTime": null,"dueEndTime":null,"reminderMinutesBefore":null,"checklistItems":[],"reason":"{{reason}}"}
            """;
        var second = first.Replace("164940d3-f222-4270-861f-53ddc586bf24", "0df01022-d3e7-4f14-9ad8-c15b498e45ed")
            .Replace(title, language == "en" ? "Dim the lights" : "Diminuir a luz")
            .Replace("\"dueTime\": null", "\"dueTime\":\"22:00:00\"").Replace("\"dueEndTime\":null", "\"dueEndTime\":\"22:15:00\"");
        var third = second.Replace("0df01022-d3e7-4f14-9ad8-c15b498e45ed", "0ebc3056-b151-46d1-b4cd-d14a8f440c62")
            .Replace(language == "en" ? "Dim the lights" : "Diminuir a luz", language == "en" ? "Stretch" : "Alongar")
            .Replace("22:15:00", "22:30:00").Replace("22:00:00", "22:15:00");
        return $$"""{"language":"{{language}}","drafts":[{{(refine ? first : string.Join(',', first, second, third))}}]}""";
    }

    private sealed class ReplayHandler(params string[] responses) : HttpMessageHandler
    {
        public int RequestCount => Requests.Count;
        public List<string> Requests { get; } = [];

        public string? CorrectionAt(int index)
        {
            using var request = JsonDocument.Parse(Requests[index]);
            using var prompt = JsonDocument.Parse(request.RootElement.GetProperty("input")[1]
                .GetProperty("content")[0].GetProperty("text").GetString()!);
            return prompt.RootElement.GetProperty("correction").GetString();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var text = responses[Math.Min(RequestCount - 1, responses.Length - 1)];
            return new(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(ResponsesTestData.Text(text), Encoding.UTF8, "application/json")
            };
        }
    }
}
