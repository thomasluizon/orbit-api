using System.ClientModel.Primitives;
using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Tests.AI;

public class AudioTranscriptionBudgetTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task EvalAudio_ReservationFailurePreventsDispatchAndRetainsQuotaError(bool quota, bool listed)
    {
        var userId = Guid.NewGuid();
        var budget = Substitute.For<IAiDailyBudget>();
        budget.ReserveAsync(userId, Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Guid>(quota ? new AiBudgetExceededException() : new IOException("Reservation unavailable")));
        var handler = new AudioCapture();
        var service = Service(userId, budget, handler, listed);
        using var audio = Wave();

        var result = await service.TranscribeAsync(audio, "recording.wav", "en");

        result.IsFailure.Should().BeTrue();
        if (quota) result.ErrorCode.Should().Be(AiBudgetExceededException.Error.Code);
        handler.Calls.Should().Be(0);
        await budget.Received(1).ReserveAsync(userId, 200, Arg.Any<CancellationToken>());
        await budget.DidNotReceiveWithAnyArgs().SettleAsync(default, default, default);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task EvalAudio_SettlesRejectionsToZeroAndRetainsUncertainDispatchSpend(bool uncertain, bool listed)
    {
        var userId = Guid.NewGuid();
        var callId = Guid.NewGuid();
        var budget = Substitute.For<IAiDailyBudget>();
        budget.ReserveAsync(userId, 200, Arg.Any<CancellationToken>()).Returns(callId);
        var handler = new AudioCapture(uncertain);
        using var audio = Wave();

        (await Service(userId, budget, handler, listed).TranscribeAsync(audio, "recording.wav", "en")).IsFailure.Should().BeTrue();

        handler.Calls.Should().Be(1);
        await budget.Received(1).SettleAsync(callId, uncertain ? 200 : 0, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvalAudio_UnknownDurationFailsBeforeProviderOrReservation()
    {
        var budget = Substitute.For<IAiDailyBudget>();
        var handler = new AudioCapture();
        using var audio = new MemoryStream([0, 1]);
        var result = await Service(Guid.NewGuid(), budget, handler).TranscribeAsync(audio, "recording.webm", "en");
        result.IsFailure.Should().BeTrue();
        handler.Calls.Should().Be(0);
        await budget.DidNotReceiveWithAnyArgs().ReserveAsync(default, default, default);
    }

    private static AudioTranscriptionService Service(Guid userId, IAiDailyBudget budget, AudioCapture handler, bool listed = true)
    {
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        http.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["StagingEval:AccountIds"] = listed ? userId.ToString() : null
        }).Build();
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(listed ? [] : [FeatureFlagKeys.AstraEvalUnmetered]);
        return new AudioTranscriptionService(Options.Create(new AiSettings
        {
            ApiKey = "production-test-key",
            EvalApiKey = "eval-test-key",
            BaseUrl = "https://orbit.test/v1",
            MaxRetries = 2
        }), NullLogger<AudioTranscriptionService>.Instance,
            new HttpClientPipelineTransport(new HttpClient(handler)), flags, config, http, budget);
    }

    internal static MemoryStream Wave()
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + 16000);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(8000);
        writer.Write(16000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(16000);
        writer.Write(new byte[16000]);
        stream.Position = 0;
        return stream;
    }

    private sealed class AudioCapture(bool uncertain = false) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (uncertain) throw new HttpRequestException("Uncertain dispatch");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}") });
        }
    }
}
