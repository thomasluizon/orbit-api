#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;

namespace Orbit.Infrastructure.Tests.AI;

public class AiCompletionDiagnosticsTests
{
    [Theory]
    [InlineData("estimate", false)]
    [InlineData("reservation", false)]
    [InlineData("completion", false)]
    [InlineData("estimate", true)]
    [InlineData("reservation", true)]
    [InlineData("completion", true)]
    public async Task BudgetedFailure_RecordsOnlyItsBoundaryAndPreservesExceptionAndSettlement(string stage, bool cancelled)
    {
        const string privateRequest = "private-request-with-prompts-and-identifiers";
        using var cancellation = new CancellationTokenSource();
        Exception failure = cancelled
            ? new OperationCanceledException("private-cancellation-detail", cancellation.Token)
            : stage == "reservation" ? new AiBudgetExceededException() : new InvalidOperationException("private-failure-detail");
        var userId = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        var order = new List<string>();
        var estimates = Substitute.For<IAiRequestCostEstimator>();
        estimates.EstimateMicroDollarsAsync(privateRequest, cancellation.Token).Returns(_ =>
        {
            order.Add("estimate");
            return stage == "estimate" ? FailAsync<long>() : Task.FromResult(1000L);
        });
        var budget = Substitute.For<IAiDailyBudget>();
        budget.ReserveAsync(userId, 1000L, cancellation.Token).Returns(_ =>
        {
            order.Add("reservation");
            return stage == "reservation" ? FailAsync<Guid>() : Task.FromResult(reservation);
        });
        budget.SettleAsync(reservation, 100L, Arg.Any<CancellationToken>()).Returns(call =>
        {
            order.Add("settlement");
            var settlementToken = call.Arg<CancellationToken>();
            settlementToken.CanBeCanceled.Should().BeTrue();
            settlementToken.IsCancellationRequested.Should().BeFalse();
            settlementToken.Should().NotBe(cancellation.Token);
            return Task.CompletedTask;
        });
        var logger = WarningLogger<BudgetedAiCompletionClient>();
        using var handler = new ResponseHandler(ResponsesTestData.Text("private-provider-output"));
        var usage = Substitute.For<IAiUsageRecorder>();
        var inner = CreateClient(handler, WarningLogger<AiCompletionClient>(), usage);
        var client = new BudgetedAiCompletionClient(inner, budget, new HttpContextAccessor(), logger, estimates);

        var thrown = await Record.ExceptionAsync(() => client.RunAsync(userId, completion =>
        {
            order.Add("completion");
            completion.Should().BeSameAs(inner);
            AiCallContext.Current.Should().NotBeNull();
            AiCallContext.Current!.UserId.Should().Be(userId);
            AiCallContext.Current.ReportCost(100L);
            return FailAsync<string>();
        }, cancellation.Token, privateRequest));

        thrown.Should().BeSameAs(failure);
        thrown!.StackTrace.Should().Contain(nameof(ThrowAtPortAsync));
        cancellation.IsCancellationRequested.Should().Be(cancelled);
        if (cancelled)
            ((OperationCanceledException)thrown).CancellationToken.Should().Be(cancellation.Token);
        order.Should().Equal(stage switch
        {
            "estimate" => ["estimate"],
            "reservation" => ["estimate", "reservation"],
            _ => new[] { "estimate", "reservation", "completion", "settlement" }
        });
        estimates.ReceivedCalls().Should().ContainSingle();
        budget.ReceivedCalls().Should().HaveCount(stage == "estimate" ? 0 : stage == "reservation" ? 1 : 2);
        if (stage == "completion")
            await budget.Received(1).SettleAsync(reservation, 100L, Arg.Any<CancellationToken>());
        AiCallContext.Current.Should().BeNull();
        handler.Calls.Should().Be(0);
        usage.ReceivedCalls().Should().BeEmpty();
        AssertReceipt(logger, new()
        {
            ["Stage"] = stage,
            ["Reason"] = cancelled ? "cancelled" : stage == "reservation" ? "budget_exceeded" : "failed"
        });

        Task<T> FailAsync<T>()
        {
            if (cancelled)
                cancellation.Cancel();
            return ThrowAtPortAsync<T>(failure);
        }
    }

    [Fact]
    public async Task BudgetedSuccess_PreservesOrderAndSettlementWithoutFailureReceipt()
    {
        var userId = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        var order = new List<string>();
        var estimates = Substitute.For<IAiRequestCostEstimator>();
        estimates.EstimateMicroDollarsAsync("private-request", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            order.Add("estimate");
            return Task.FromResult(1000L);
        });
        var budget = Substitute.For<IAiDailyBudget>();
        budget.ReserveAsync(userId, 1000L, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            order.Add("reservation");
            return Task.FromResult(reservation);
        });
        budget.SettleAsync(reservation, 100L, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            order.Add("settlement");
            return Task.CompletedTask;
        });
        var logger = WarningLogger<BudgetedAiCompletionClient>();
        using var handler = new ResponseHandler(ResponsesTestData.Text("private-provider-output"));
        var inner = CreateClient(handler, WarningLogger<AiCompletionClient>(), Substitute.For<IAiUsageRecorder>());
        var client = new BudgetedAiCompletionClient(inner, budget, new HttpContextAccessor(), logger, estimates);

        var result = await client.RunAsync(userId, _ =>
        {
            order.Add("completion");
            AiCallContext.Current!.ReportCost(100L);
            return Task.FromResult("private-result");
        }, default, "private-request");

        result.Should().Be("private-result");
        order.Should().Equal("estimate", "reservation", "completion", "settlement");
        await budget.Received(1).SettleAsync(reservation, 100L, Arg.Any<CancellationToken>());
        LogCalls(logger).Should().BeEmpty();
        AiCallContext.Current.Should().BeNull();
    }

    [Theory]
    [InlineData(ResponseStatus.Completed, false)]
    [InlineData(ResponseStatus.Completed, true)]
    [InlineData(ResponseStatus.Incomplete, false)]
    [InlineData(ResponseStatus.Incomplete, true)]
    [InlineData(ResponseStatus.Failed, false)]
    [InlineData(ResponseStatus.Failed, true)]
    [InlineData(ResponseStatus.InProgress, true)]
    [InlineData(ResponseStatus.Cancelled, true)]
    [InlineData(ResponseStatus.Queued, true)]
    [InlineData(null, false)]
    [InlineData(null, true)]
    public async Task JsonStatus_RecordsOnlyRejectedStatusAndPreservesRequireCompleteBehavior(ResponseStatus? status, bool requireComplete)
    {
        var response = ResponsesTestData.Response("{\"value\":\"private-provider-output\"}");
        response.Status = status;
        var serialized = ModelReaderWriter.Write(response);
        ModelReaderWriter.Read<ResponseResult>(serialized)!.Status.Should().Be(status);
        using var handler = new ResponseHandler(serialized.ToString());
        var logger = WarningLogger<AiCompletionClient>();
        var usage = Substitute.For<IAiUsageRecorder>();
        var client = CreateClient(handler, logger, usage);
        Probe? result = null;

        var thrown = await Record.ExceptionAsync(async () => result = await client.CompleteJsonAsync<Probe>(
            "private-system-prompt", "private-user-prompt", userId: Guid.NewGuid(), requireComplete: requireComplete));

        handler.Calls.Should().Be(1);
        usage.ReceivedCalls().Should().ContainSingle();
        if (status == ResponseStatus.Completed || status == ResponseStatus.Incomplete && !requireComplete)
        {
            thrown.Should().BeNull();
            result!.Value.Should().Be("private-provider-output");
            LogCalls(logger).Should().BeEmpty();
        }
        else
        {
            thrown.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be(status == ResponseStatus.Incomplete
                ? "AI date interpretation did not complete."
                : "AI response did not complete successfully.");
            result.Should().BeNull();
            AssertReceipt(logger, new()
            {
                ["Stage"] = "json_response",
                ["Reason"] = "not_completed",
                ["Status"] = status == ResponseStatus.Incomplete ? "incomplete" : "other"
            });
        }
    }

    private static async Task<T> ThrowAtPortAsync<T>(Exception failure)
    {
        await Task.Yield();
        throw failure;
    }

    private static ILogger<T> WarningLogger<T>()
    {
        var logger = Substitute.For<ILogger<T>>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);
        return logger;
    }

    private static IEnumerable<object?[]> LogCalls<T>(ILogger<T> logger) => logger.ReceivedCalls()
        .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log)).Select(call => call.GetArguments());

    private static void AssertReceipt<T>(ILogger<T> logger, Dictionary<string, object?> expected)
    {
        var entry = LogCalls(logger).Should().ContainSingle("the failing owner must record its boundary").Which;
        entry[0].Should().Be(LogLevel.Warning);
        entry[3].Should().BeNull("diagnostics must never attach an exception");
        var state = ((IEnumerable<KeyValuePair<string, object?>>)entry[2]!).ToDictionary(pair => pair.Key, pair => pair.Value);
        state.Remove("{OriginalFormat}", out var template).Should().BeTrue();
        template.Should().BeOfType<string>().Which.Should().NotContain("private");
        state.Should().BeEquivalentTo(expected, "the receipt contains only fixed classifications");
    }

    private static AiCompletionClient CreateClient(HttpMessageHandler handler, ILogger<AiCompletionClient> logger, IAiUsageRecorder usage) =>
        new(new ResponsesClient(new ApiKeyCredential("private-test-credential"), new ResponsesClientOptions
        {
            Endpoint = new Uri("https://orbit.test/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler))
        }), logger, usage);

    private sealed record Probe(string Value);

    private sealed class ResponseHandler(string response) : HttpMessageHandler
    {
        internal int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }
    }
}
