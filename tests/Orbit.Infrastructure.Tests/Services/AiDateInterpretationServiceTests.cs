#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OpenAI.Responses;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.AI;

namespace Orbit.Infrastructure.Tests.Services;

public class AiDateInterpretationServiceTests
{
    private static readonly DateOnly Reference = new(2026, 10, 20);
    private static readonly Guid UserId = Guid.NewGuid();

    [Theory]
    [InlineData("{\"date\":\"2028-02-29\"}", "2028-02-29")]
    [InlineData("{\"date\":null}", null)]
    [InlineData("{\"date\":\"2027-02-29\"}", null)]
    [InlineData("{\"date\":\"2026-1-01\"}", null)]
    [InlineData("{\"date\":\"2026-01-01\",\"action\":\"delete\"}", null)]
    [InlineData("{\"date\":\"2026-01-01\",\"date\":null}", null)]
    [InlineData("{\"referenceDate\":\"2026-01-01\"}", null)]
    [InlineData("{\"date\":42}", null)]
    [InlineData("{}", null)]
    [InlineData("[]", null)]
    [InlineData("{\"date\":", null)]
    [InlineData("here is your date: 2026-01-01", null)]
    public async Task ProviderReply_OnlyAcceptsOneCanonicalDateProperty(string output, string? expected)
    {
        var handler = new ResponseHandler(output);
        var service = Build(handler);
        var result = await service.InterpretAsync(UserId,
            DateInterpretationInput.Create("after the next holiday", "en").Value, Reference, TimeZoneInfo.Utc, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value?.ToString("yyyy-MM-dd").Should().Be(expected);
        handler.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task Consultation_UsesBudgetedConfiguredSubTaskAndSmallOutputCap(string language)
    {
        var handler = new ResponseHandler("{\"date\":\"2026-10-26\"}");
        var service = Build(handler);
        var result = await service.InterpretAsync(UserId,
            DateInterpretationInput.Create("next holiday", language).Value, Reference, TimeZoneInfo.Utc, CancellationToken.None);
        result.Value.Should().Be(new DateOnly(2026, 10, 26));
        using var request = JsonDocument.Parse(handler.Request!);
        request.RootElement.GetProperty("max_output_tokens").GetInt32().Should().Be(AiDateInterpretationService.MaxOutputTokens);
        request.RootElement.GetProperty("model").GetString().Should().Be("gpt-6-luna");
        request.RootElement.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("low");
        handler.Request.Should().Contain("2026-10-20").And.Contain("UTC").And.Contain(language);
        Encoding.UTF8.GetByteCount("{\"date\":\"2026-10-26\"}").Should().BeLessThan(AiDateInterpretationService.MaxOutputTokens);
        Encoding.UTF8.GetByteCount("{\"date\":null}").Should().BeLessThan(AiDateInterpretationService.MaxOutputTokens);
    }

    [Fact]
    public async Task HardUnresolvedInput_CanonicalProposalCannotOverridePreflight()
    {
        var handler = new ResponseHandler("{\"date\":\"2026-10-26\"}");
        var result = await Build(handler).InterpretAsync(UserId,
            DateInterpretationInput.Create("February 29, 10000", "en").Value,
            Reference, TimeZoneInfo.Utc, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task SpendRefusal_RetainsTypedErrorWithoutDispatch()
    {
        var handler = new ResponseHandler("{\"date\":\"2026-10-26\"}");
        var budget = Substitute.For<IAiDailyBudget>();
        budget.ReserveAsync(UserId, Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AiBudgetExceededException());
        var service = Build(handler, budget);
        var result = await service.InterpretAsync(UserId, DateInterpretationInput.Create("next holiday", "en").Value,
            Reference, TimeZoneInfo.Utc, CancellationToken.None);
        result.ErrorCode.Should().Be(AiBudgetExceededException.Error.Code);
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task IncompleteProviderReply_CannotSupplyDate()
    {
        var handler = new ResponseHandler("{\"date\":\"2026-10-26\"}", incomplete: true);
        var result = await Build(handler).InterpretAsync(UserId, DateInterpretationInput.Create("next holiday", "en").Value,
            Reference, TimeZoneInfo.Utc, CancellationToken.None);
        result.ErrorCode.Should().Be(ErrorMessages.AiUnavailable.Code);
        handler.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutAndCallerCancellation_DoNotSupplyDateOrRetry(bool callerCancellation)
    {
        var handler = new ResponseHandler("unused", wait: true);
        using var cancellation = new CancellationTokenSource();
        var service = Build(handler);
        var task = service.InterpretAsync(UserId, DateInterpretationInput.Create("next holiday", "en").Value,
            Reference, TimeZoneInfo.Utc, cancellation.Token);
        if (callerCancellation)
        {
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Func<Task> call = async () => await task;
            await call.Should().ThrowAsync<OperationCanceledException>();
        }
        else
            (await task).ErrorCode.Should().Be(ErrorMessages.AiUnavailable.Code);
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ProviderError_RetainsTypedFailure()
    {
        var handler = new ResponseHandler("{}", status: HttpStatusCode.Unauthorized);
        var result = await Build(handler).InterpretAsync(UserId, DateInterpretationInput.Create("next holiday", "en").Value,
            Reference, TimeZoneInfo.Utc, CancellationToken.None);
        result.ErrorCode.Should().Be(ErrorMessages.AiUnavailable.Code);
    }

    private static AiDateInterpretationService Build(ResponseHandler handler, IAiDailyBudget? budget = null)
    {
        var sdk = new ResponsesClient(new ApiKeyCredential("test-key"), new ResponsesClientOptions
        {
            Endpoint = new Uri("https://orbit.test/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(0)
        });
        var inner = new AiCompletionClient(sdk, NullLogger<AiCompletionClient>.Instance,
            Substitute.For<IAiUsageRecorder>(), completionTimeout: TimeSpan.FromMilliseconds(200));
        var estimates = Substitute.For<IAiRequestCostEstimator>();
        estimates.EstimateMicroDollarsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(1000L);
        var client = new BudgetedAiCompletionClient(inner, budget ?? Substitute.For<IAiDailyBudget>(),
            new HttpContextAccessor(), NullLogger<BudgetedAiCompletionClient>.Instance, estimates);
        return new AiDateInterpretationService(client);
    }

    private sealed class ResponseHandler(string output, bool incomplete = false, bool wait = false,
        HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Request { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Request = await request.Content!.ReadAsStringAsync(cancellationToken);
            Started.TrySetResult();
            if (wait)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var response = ResponsesTestData.Response(output);
            if (incomplete)
                response.Status = ResponseStatus.Incomplete;
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(ModelReaderWriter.Write(response).ToString(), Encoding.UTF8, "application/json")
            };
        }
    }
}
