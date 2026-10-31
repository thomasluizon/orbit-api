#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
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

public sealed class AiRetrospectiveGenerationTests
{
    private static readonly DateOnly Day = new(2025, 4, 1);

    [Theory]
    [InlineData("en", "```\n```")]
    [InlineData("pt-BR", "```\n```")]
    [InlineData("en", "Highlights\nMissed Opportunities\nTrends\nSuggestion")]
    [InlineData("pt-BR", "Destaques\nOportunidades Perdidas\nTendências\nSugestão")]
    public async Task Retrospective_NormalizedEmptyOutputReturnsFailure(string language, string reply)
    {
        var service = Service(reply);
        var result = await service.GenerateRetrospectiveAsync([Habit()], Day, Day, "week", language);
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.AiRetrospectiveUnavailable.Message);
    }

    [Theory]
    [InlineData("en", "Psyllium 5g is part of your routine.")]
    [InlineData("pt-BR", "Psyllium 5g faz parte da sua rotina.")]
    public async Task Retrospective_DigitsInStoredTitleRemainVisible(string language, string reply)
    {
        var result = await Service(reply).GenerateRetrospectiveAsync([Habit()], Day, Day, "week", language);
        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Highlights.Should().Be(reply);
    }

    private static Habit Habit() => Orbit.Domain.Entities.Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
        Guid.NewGuid(), "Psyllium 5g", FrequencyUnit.Day, 1, DueDate: Day)).Value;

    private static AiRetrospectiveService Service(string reply)
    {
        var client = new ResponsesClient(new ApiKeyCredential("test-key"), new ResponsesClientOptions
        {
            Endpoint = new Uri("https://orbit.test/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(new ResponseHandler(reply)))
        });
        var completion = new AiCompletionClient(client, NullLogger<AiCompletionClient>.Instance, Substitute.For<IAiUsageRecorder>());
        return new(AiBudgetTestDouble.Wrap(completion), NullLogger<AiRetrospectiveService>.Instance);
    }

    private sealed class ResponseHandler(string reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(ResponsesTestData.Text(reply), Encoding.UTF8, "application/json")
            });
    }
}
