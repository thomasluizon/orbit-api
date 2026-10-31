#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.Extensions;
using Orbit.Api.Middleware;
using Orbit.Application.Common;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.AI;

public class AiProviderQuotaTests
{
    private const string QuotaCode = "AI_PROVIDER_QUOTA_EXHAUSTED";
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 10, 1);

    [Theory]
    [InlineData("insufficient_quota")]
    [InlineData("credit_balance_exhausted")]
    public async Task Summary_QuotaFailure_ReachesHttpBoundaryAfterOneAttempt(string providerCode)
    {
        var (completion, handler, logger) = CreateCompletion(providerCode);
        var service = new AiSummaryService(AiBudgetTestDouble.Wrap(completion), NullLogger<AiSummaryService>.Instance);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetDailySummaryQuery>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var query = call.Arg<GetDailySummaryQuery>();
            var generated = await service.GenerateSummaryAsync([], new DailySummaryContext(
                query.DateFrom, query.DateTo, Day, query.Language, null, 0, 0, new Dictionary<Guid, DateOnly>(), UserId: UserId));
            return generated.PropagateError<DailySummaryResponse>();
        });
        var controller = new HabitsController(mediator, NullLogger<HabitsController>.Instance, Substitute.For<IUserDateService>())
        {
            ControllerContext = Context()
        };

        AssertQuotaResult(await controller.GetDailySummary(Day, Day));

        AssertOneAttempt(handler, logger);
    }

    [Theory]
    [InlineData(false, "insufficient_quota")]
    [InlineData(true, "insufficient_quota")]
    [InlineData(false, "credit_balance_exhausted")]
    [InlineData(true, "credit_balance_exhausted")]
    public async Task Chat_QuotaFailure_PreservesTypedErrorForBufferedAndStreamedCalls(bool streaming, string providerCode)
    {
        var (completion, handler, logger) = CreateCompletion(providerCode);
        var service = new AiIntentService(AiBudgetTestDouble.Wrap(completion), Substitute.For<IAiUsageRecorder>(),
            NullLogger<AiIntentService>.Instance);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            var generated = await service.SendWithToolsAsync(new AiToolRequest("Hello", "system", [], UserId),
                streaming ? _ => Task.CompletedTask : null);
            return generated.PropagateError<ChatResponse>();
        });
        var controller = new ChatController(mediator, Substitute.For<IImageValidationService>(), NullLogger<ChatController>.Instance)
        {
            ControllerContext = Context()
        };

        if (streaming)
        {
            controller.Response.Body = new MemoryStream();
            await controller.ProcessChatStream("Hello", null, null, CancellationToken.None);
            controller.Response.Body.Position = 0;
            var events = await new StreamReader(controller.Response.Body).ReadToEndAsync();
            events.Should().Contain(QuotaCode).And.Contain("424");
        }
        else
        {
            AssertQuotaResult(await controller.ProcessChat("Hello", null, null, CancellationToken.None));
        }

        AssertOneAttempt(handler, logger);
    }

    [Theory]
    [InlineData("retrospective")]
    [InlineData("goal_review")]
    [InlineData("refine")]
    [InlineData("setup")]
    [InlineData("tags")]
    [InlineData("reschedule")]
    [InlineData("date")]
    public async Task AiReads_QuotaFailure_PreservesTypedError(string feature)
    {
        var (completion, handler, logger) = CreateCompletion("insufficient_quota");
        var budgeted = AiBudgetTestDouble.Wrap(completion);
        using var context = AiCallContext.Begin(UserId);
        Result result = feature switch
        {
            "retrospective" => await new AiRetrospectiveService(budgeted, NullLogger<AiRetrospectiveService>.Instance)
                .GenerateRetrospectiveAsync([Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId,
                    "Walk", FrequencyUnit.Day, 1, DueDate: Day)).Value], Day, Day, "week", "en"),
            "goal_review" => await new AiGoalReviewService(budgeted, NullLogger<AiGoalReviewService>.Instance)
                .GenerateReviewAsync("goal facts", "en"),
            "refine" => await new AiHabitDraftGenerator(budgeted, NullLogger<AiHabitDraftGenerator>.Instance)
                .GenerateAsync(new HabitSuggestionRequest(UserId, "instruction", "en", 1, Day, [], []), CancellationToken.None),
            "setup" => await new AiHabitSuggestionService(budgeted, NullLogger<AiHabitSuggestionService>.Instance)
                .SuggestSetupAsync("Walk", "en"),
            "reschedule" => await new AiRescheduleSuggestionService(budgeted, NullLogger<AiRescheduleSuggestionService>.Instance)
                .GenerateAsync(Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, UserId,
                    "Walk", null, null, DueDate: Day)).Value, Day, "en"),
            "date" => await new AiDateInterpretationService(budgeted).InterpretAsync(UserId,
                DateInterpretationInput.Create("next week", "en").Value, Day, TimeZoneInfo.Utc, CancellationToken.None),
            "tags" => await new AiTagSuggestionService(budgeted, NullLogger<AiTagSuggestionService>.Instance)
                .SuggestTagsAsync("Walk", null, [], "en"),
            _ => throw new ArgumentOutOfRangeException(nameof(feature))
        };

        AssertQuotaResult(result.ToErrorResult());
        AssertOneAttempt(handler, logger);
    }

    [Theory]
    [InlineData("insufficient_quota")]
    [InlineData("credit_balance_exhausted")]
    public async Task Transcription_QuotaFailure_ReturnsTypedErrorAfterOneAttempt(string providerCode)
    {
        var handler = new QuotaHandler(providerCode);
        var logger = new AttemptLogger();
        var service = new AudioTranscriptionService(Options.Create(new AiSettings
        {
            ApiKey = "test-key",
            BaseUrl = "https://orbit.test/v1"
        }), logger, new HttpClientPipelineTransport(new HttpClient(handler)), Substitute.For<IFeatureFlagService>());
        using var audio = new MemoryStream([1, 2, 3]);

        var result = await service.TranscribeAsync(audio, "clip.webm", "en");

        AssertQuotaResult(result.ToErrorResult());
        AssertOneAttempt(handler, logger);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuotaError_ReusesLocalizedUnavailableCopy(bool portuguese)
    {
        ErrorCopy.Resolve(QuotaCode, portuguese).Should().Be(ErrorCopy.Resolve(ErrorCodes.AiUnavailable, portuguese));
    }

    private static ControllerContext Context() => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId.ToString())], "Test"))
        }
    };

    private static void AssertQuotaResult(IActionResult result)
    {
        var response = result.Should().BeOfType<ObjectResult>().Subject;
        response.StatusCode.Should().Be(424);
        response.Value.Should().BeOfType<ErrorResponse>().Which.ErrorCode.Should().Be(QuotaCode);
    }

    private static void AssertOneAttempt(QuotaHandler handler, AttemptLogger logger)
    {
        handler.CallCount.Should().Be(1);
        logger.Entries.Should().ContainSingle().Which.Should().Contain("AI attempt 1 failed (HTTP 429); retriable: False");
    }

    private static (AiCompletionClient Completion, QuotaHandler Handler, AttemptLogger Logger) CreateCompletion(string code)
    {
        var handler = new QuotaHandler(code);
        var logger = new AttemptLogger();
        var completion = new AiCompletionClient(Options.Create(new AiSettings { ApiKey = "test-key", BaseUrl = "https://orbit.test/v1" }),
            NullLogger<AiCompletionClient>.Instance, Substitute.For<IAiUsageRecorder>(),
            new HttpClientPipelineTransport(new HttpClient(handler)), retryPolicy: new ZeroDelayRetryPolicy(logger));
        return (completion, handler, logger);
    }

    private sealed class ZeroDelayRetryPolicy(ILogger logger) : AiRetryLoggingPolicy(2, logger)
    {
        protected override TimeSpan GetNextDelay(PipelineMessage message, int tryCount) => TimeSpan.Zero;
    }

    private sealed class QuotaHandler(string code) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                RequestMessage = request,
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    error = new { message = "You exceeded your current quota", type = "insufficient_quota", code, param = (string?)null }
                }), Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class AttemptLogger : ILogger<AudioTranscriptionService>
    {
        public List<string> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add(formatter(state, exception));
    }
}
