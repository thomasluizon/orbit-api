using System.ClientModel.Primitives;
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Tests.AI;

public partial class AiDailyBudgetTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FlagOnlyEvalAudio_ReservesSharedCeilingBeforeDispatchAndUsesEvalKey(bool exhausted)
    {
        using var fixture = new BudgetFixture(pro: true, evalExempt: true, listed: false);
        using var scope = fixture.CreateScope();
        var flags = scope.ServiceProvider.GetRequiredService<IFeatureFlagService>();
        (await flags.GetEnabledKeysForUserAsync(fixture.User.Id))
            .Should().Contain(FeatureFlagKeys.AstraEvalUnmetered);
        EvalAccountAccess.Contains(fixture.AccountConfiguration[EvalAccountAccess.AccountIdsKey], fixture.User.Id)
            .Should().BeFalse();
        await fixture.Budget.ReserveAsync(fixture.User.Id, exhausted ? 500_000 : 499_800, default);
        var handler = new AudioCredentialCapture();
        var service = new AudioTranscriptionService(Options.Create(new AiSettings
        {
            ApiKey = "production-test-key",
            EvalApiKey = "eval-test-key",
            BaseUrl = "https://orbit.test/v1",
            MaxRetries = 2
        }), NullLogger<AudioTranscriptionService>.Instance,
            new HttpClientPipelineTransport(new HttpClient(handler)), flags,
            fixture.AccountConfiguration, fixture.Http, fixture.Budget);
        using var audio = AudioTranscriptionBudgetTests.Wave();

        var result = await service.TranscribeAsync(audio, "recording.wav", "en");

        if (exhausted)
        {
            result.ErrorCode.Should().Be(AiBudgetExceededException.Error.Code);
            handler.Credentials.Should().BeEmpty();
        }
        else
        {
            handler.Credentials.Should().Equal("eval-test-key");
        }
        using var read = fixture.Database.CreateContext();
        var calls = read.AiBudgetCalls.ToList();
        calls.Should().HaveCount(exhausted ? 1 : 2);
        calls.Should().OnlyContain(call => call.EvalDateUtc == Today);
        if (!exhausted)
            calls.Should().ContainSingle(call => call.IsSettled && call.ChargedMicroDollars == 0);
    }

    private sealed class AudioCredentialCapture : HttpMessageHandler
    {
        internal List<string?> Credentials { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Credentials.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}") });
        }
    }
}
