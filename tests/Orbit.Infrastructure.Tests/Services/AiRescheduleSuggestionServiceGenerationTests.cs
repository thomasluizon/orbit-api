#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services;

using Orbit.Infrastructure.Tests.AI;

namespace Orbit.Infrastructure.Tests.Services;

public class AiRescheduleSuggestionServiceGenerationTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 7, 13);
    private static readonly string[] SuggestedDayNames = new[] { "Monday", "Tuesday" };

    [Fact]
    public async Task GenerateAsync_ReservesOutputTokensForReasoningAndJson()
    {
        string? body = null;
        var service = BuildService("{\"dueDate\":\"2026-07-20\",\"rationale\":\"Restart gently.\"}",
            captureRequest: request => body = request);

        var result = await service.GenerateAsync(OverdueHabit(), Today, "en");

        result.IsSuccess.Should().BeTrue();
        using var document = JsonDocument.Parse(body!);
        document.RootElement.GetProperty("model").GetString().Should().Be("gpt-6-luna");
        document.RootElement.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("low");
        document.RootElement.GetProperty("max_output_tokens").GetInt32().Should().Be(8192);
    }

    [Fact]
    public async Task GenerateAsync_ValidPayload_ReturnsClampedSuggestion()
    {
        var service = BuildService(JsonSerializer.Serialize(new
        {
            frequencyUnit = "Day",
            frequencyQuantity = 1,
            dueDate = "2026-07-20",
            dueTime = "08:00",
            days = SuggestedDayNames,
            rationale = "  Let's **ease back** in *gently*.  "
        }));

        var result = await service.GenerateAsync(OverdueHabit(), Today, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.FrequencyUnit.Should().Be(FrequencyUnit.Day);
        result.Value.FrequencyQuantity.Should().Be(1);
        result.Value.DueDate.Should().Be(new DateOnly(2026, 7, 20));
        result.Value.DueTime.Should().Be(new TimeOnly(8, 0));
        result.Value.Days.Should().Equal(DayOfWeek.Monday, DayOfWeek.Tuesday);
        result.Value.Rationale.Should().Be("Let's ease back in gently.");
    }

    [Fact]
    public async Task GenerateAsync_RationalePreservesLiteralHabitName()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Run *gently*", FrequencyUnit.Day, 1,
            DueDate: Today.AddDays(-5))).Value;
        var service = BuildService(JsonSerializer.Serialize(new
        {
            dueDate = "2026-07-20",
            rationale = "Try **Run *gently*** again."
        }));

        var result = await service.GenerateAsync(habit, Today, "en");

        result.Value.Rationale.Should().Be("Try Run *gently* again.");
    }

    [Fact]
    public async Task GenerateAsync_EmptyCompletion_ReturnsEmptyResponseFailure()
    {
        var service = BuildService("   ");

        var result = await service.GenerateAsync(OverdueHabit(), Today, "en");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.AiEmptyResponse.Message);
    }

    [Fact]
    public async Task GenerateAsync_BlankRationale_ReturnsEmptyResponseFailure()
    {
        var service = BuildService(JsonSerializer.Serialize(new { dueDate = "2026-07-20", rationale = "   " }));

        var result = await service.GenerateAsync(OverdueHabit(), Today, "en");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.AiEmptyResponse.Message);
    }

    [Fact]
    public async Task GenerateAsync_UnparseableDueDate_ReturnsRescheduleUnavailable()
    {
        var service = BuildService(JsonSerializer.Serialize(new
        {
            dueDate = "not-a-date",
            rationale = "Restart tomorrow, you've got this."
        }));

        var result = await service.GenerateAsync(OverdueHabit(), Today, "en");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.AiRescheduleUnavailable.Message);
    }

    [Fact]
    public async Task GenerateAsync_AiCallFails_ReturnsRescheduleUnavailable()
    {
        var service = BuildService("boom", HttpStatusCode.BadRequest);

        var result = await service.GenerateAsync(OverdueHabit(), Today, "en");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.AiRescheduleUnavailable.Message);
    }

    [Fact]
    public async Task GenerateAsync_OverlongRationale_CapsToSentenceWithinLimit()
    {
        var longRationale = new string('a', 120) + ". " + new string('b', 150);
        var service = BuildService(JsonSerializer.Serialize(new { dueDate = "2026-07-20", rationale = longRationale }));

        var result = await service.GenerateAsync(OverdueHabit(), Today, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Rationale.Length.Should().BeLessThanOrEqualTo(240);
        result.Value.Rationale.Should().EndWith(".");
    }

    private static Habit OverdueHabit() =>
        Habit.Create(new HabitCreateParams(
            UserId, "Evening run", FrequencyUnit.Day, 1, DueDate: Today.AddDays(-5))).Value;

    private static AiRescheduleSuggestionService BuildService(string content, HttpStatusCode status = HttpStatusCode.OK,
        Action<string>? captureRequest = null)
    {
        var chatClient = new ResponsesClient(
            credential: new ApiKeyCredential("test-key"),
            options: new ResponsesClientOptions
            {
                Endpoint = new Uri("https://orbit.test/v1"),
                Transport = new HttpClientPipelineTransport(
                    new HttpClient(new CannedChatHandler(content, status, captureRequest))),
            });
        var aiClient = new AiCompletionClient(
            chatClient, NullLogger<AiCompletionClient>.Instance, Substitute.For<IAiUsageRecorder>());
        return new AiRescheduleSuggestionService(Orbit.Infrastructure.Tests.AiBudgetTestDouble.Wrap(aiClient), NullLogger<AiRescheduleSuggestionService>.Instance);
    }

    private sealed class CannedChatHandler(string content, HttpStatusCode status, Action<string>? captureRequest) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            captureRequest?.Invoke(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (status != HttpStatusCode.OK)
                return new HttpResponseMessage(status)
                {
                    RequestMessage = request,
                    Content = new StringContent("{\"error\":{\"message\":\"bad\"}}", Encoding.UTF8, "application/json"),
                };

            var body = ResponsesTestData.Text(content);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
