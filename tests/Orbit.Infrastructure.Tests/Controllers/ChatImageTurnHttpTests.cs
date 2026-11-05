using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Asp.Versioning;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.Extensions;
using Orbit.Api.Idempotency;
using Orbit.Api.RateLimiting;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Controllers;

/// <summary>
/// Drives an Android image turn through the real HTTP path of <c>POST /api/chat/stream</c>, with the body that
/// expo/fetch builds, up to the handler that makes the first model call.
/// https://github.com/thomasluizon/orbit-tickets/issues/2036
/// </summary>
public sealed class ChatImageTurnHttpTests
{
    private const int ScreenshotWidth = 1024;
    private const int ScreenshotHeight = 2276;
    private const int ScreenshotBytes = 400 * 1024;

    private static readonly string[] MobilePartKinds =
    [
        "habitList", "goalList", "metrics", "periodTotal", "periodComparison", "periodInsight", "daySummary",
        "streak", "gamification", "calendar", "recordList", "accountRows", "choice", "pickList", "question",
        "habitRanking", "habitMonth", "habitSuggestions", "oneSmallStep"
    ];

    [Theory]
    [InlineData("Screenshot_20261105-131052_Cinesystem.jpg")]
    [InlineData("image.jpg")]
    [InlineData("Captura%20de%20tela%202026-11-05%20131052.jpg")]
    public async Task AndroidScreenshotTurn_ReachesTheChatHandlerWithTheImageBytes(string encodedFileName)
    {
        var image = ScreenshotJpeg();
        await using var factory = new ChatImageApiFactory();
        using var client = factory.CreateClient();
        using var request = AndroidStreamRequest(image, encodedFileName, out var conversationId);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        var events = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(frame => JsonDocument.Parse(frame["data: ".Length..]).RootElement.GetProperty("type").GetString())
            .ToArray();
        events.Should().Equal("started", "final");

        var command = factory.Commands.Should().ContainSingle().Subject;
        command.Message.Should().Be("cria pra mim");
        command.ImageMimeType.Should().Be("image/jpeg");
        command.ImageData.Should().Equal(image);
        command.TurnId.Should().NotBeNull();
        command.StreamSink.Should().NotBeNull();
        command.ClientContext!.Locale.Should().Be("pt-BR");
        command.ClientContext.ConversationId.Should().Be(conversationId);
        factory.CompletedTerminalEvents.Should().ContainSingle().Which.Should().Contain("\"type\":\"final\"");
    }

    private static HttpRequestMessage AndroidStreamRequest(byte[] image, string encodedFileName, out Guid conversationId)
    {
        conversationId = Guid.NewGuid();
        var clientContext = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["platform"] = "mobile",
            ["locale"] = "pt-BR",
            ["timeFormat"] = "24h",
            ["currentAppArea"] = "chat",
            ["supportsHabitListCard"] = true,
            ["supportsHabitListDoneStatus"] = true,
            ["supportsDatedHabitListCard"] = true,
            ["supportsSkippedHabitRows"] = true,
            ["supportsHabitHistoryCard"] = true,
            ["supportsGoalListCard"] = true,
            ["supportsMetricsCard"] = true,
            ["supportsPeriodInsightCard"] = true,
            ["supportsDaySummaryCard"] = true,
            ["supportsStreakCard"] = true,
            ["supportsCalendarCard"] = true,
            ["supportsRecordListCard"] = true,
            ["supportsAccountRowsCard"] = true,
            ["supportsPendingOperationChanges"] = true,
            ["supportsPendingOperationPlacement"] = true,
            ["supportsToolSteps"] = true,
            ["supportsFollowUps"] = true,
            ["supportsFollowUpContext"] = true,
            ["supportsStartingStepContext"] = true,
            ["supportsMessageAllowance"] = true,
            ["supportsCompletedHabitsMetric"] = true,
            ["supportsParts"] = true,
            ["partKinds"] = MobilePartKinds,
            ["supportsProactiveOffers"] = true,
            ["conversationId"] = conversationId,
            ["dismissedOfferKeys"] = Array.Empty<string>(),
            ["selectedOfferKey"] = null
        });
        const string boundary = "----ExpoFetchFormBoundaryq8Lr2Tn5Vb7Xc1Zs";
        var body = new MemoryStream();
        void Text(string value) => body.Write(Encoding.UTF8.GetBytes(value));
        void Field(string name, string value)
        {
            Text($"--{boundary}\r\ncontent-disposition: form-data; name=\"{name}\"\r\n\r\n");
            Text(value);
            Text("\r\n");
        }
        Field("message", "cria pra mim");
        Text($"--{boundary}\r\ncontent-disposition: form-data; name=\"image\"; filename=\"{encodedFileName}\"\r\n");
        Text("content-type: image/jpeg\r\n\r\n");
        body.Write(image);
        Text("\r\n");
        Field("history", "[]");
        Field("clientContext", clientContext);
        Text($"--{boundary}--\r\n");

        var content = new ByteArrayContent(body.ToArray());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={boundary}");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/stream") { Content = content };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add("X-Orbit-Event-Origin", Guid.NewGuid().ToString());
        request.Headers.Add("X-Orbit-Time-Zone", "America/Sao_Paulo");
        return request;
    }

    /// <summary>
    /// A baseline JFIF stream with the frame size of the resized Android screenshot. The API reads only the file
    /// signature and the bytes, so the scan data is filler.
    /// </summary>
    private static byte[] ScreenshotJpeg()
    {
        byte[] header =
        [
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0xFF, 0xC0, 0x00, 0x11, 0x08,
            ScreenshotHeight >> 8, ScreenshotHeight & 0xFF, ScreenshotWidth >> 8, ScreenshotWidth & 0xFF,
            0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
            0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00
        ];
        var image = new byte[ScreenshotBytes];
        new Random(2036).NextBytes(image);
        for (var index = header.Length; index < image.Length - 2; index++)
            if (image[index] == 0xFF) image[index] = 0x00;
        header.CopyTo(image, 0);
        image[^2] = 0xFF;
        image[^1] = 0xD9;
        return image;
    }

    private sealed class ChatImageApiFactory : WebApplicationFactory<ChatController>
    {
        private readonly Guid _userId = Guid.NewGuid();
        private readonly RecordingTurnStore _turns = new();
        internal List<ProcessUserChatCommand> Commands { get; } = [];
        internal IReadOnlyList<string> CompletedTerminalEvents => _turns.TerminalEvents;

        protected override IHostBuilder CreateHostBuilder() => Host.CreateDefaultBuilder()
            .UseEnvironment("Staging")
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHost(web => web.ConfigureServices(services =>
            {
                services.AddControllers().AddApplicationPart(typeof(ChatController).Assembly);
                services.AddApiVersioning(options =>
                {
                    options.DefaultApiVersion = new ApiVersion(1, 0);
                    options.AssumeDefaultVersionWhenUnspecified = true;
                    options.ApiVersionReader = new HeaderApiVersionReader("api-version");
                }).AddMvc();
                services.AddAuthentication("JwtBearer").AddScheme<AuthenticationSchemeOptions, ChatAuthenticationHandler>("JwtBearer", _ => { });
                services.AddAuthorization();
                services.AddSingleton<IImageValidationService, ImageValidationService>();
                var mediator = Substitute.For<IMediator>();
                mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
                {
                    Commands.Add(call.Arg<ProcessUserChatCommand>());
                    return Result.Success(new ChatResponse("Pronto.", []));
                });
                services.AddSingleton(mediator);
                var limits = Substitute.For<IDistributedRateLimitService>();
                var allowed = new DistributedRateLimitDecision(true, 20, 1, DateTime.UtcNow.AddMinutes(5));
                limits.TryAcquireAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(allowed);
                limits.TryAcquireLeaseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<TimeSpan>(),
                    Arg.Any<CancellationToken>()).Returns(allowed);
                services.AddSingleton(limits);
                services.AddSingleton(Substitute.For<IAuthSessionService>());
                services.AddSingleton<BffClientIpResolver>();
                services.AddSingleton<IChatTurnStore>(_turns);
                services.AddOrbitShutdownDrain();
                services.AddScoped<ChatTurnStream>();
            }).Configure(app =>
            {
                app.Use(async (context, next) =>
                {
                    context.Request.Headers["X-Test-User"] = _userId.ToString();
                    await next(context);
                });
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            }));
    }

    private sealed class RecordingTurnStore : IChatTurnStore
    {
        private readonly List<string> _terminalEvents = [];
        private ChatTurn? _turn;
        internal IReadOnlyList<string> TerminalEvents => _terminalEvents;

        public Task<(ChatTurn Turn, bool Claimed)> ClaimAsync(ChatTurn candidate, CancellationToken cancellationToken)
        {
            _turn = candidate;
            return Task.FromResult((candidate, true));
        }

        public Task<ChatTurn> ReadAsync(Guid userId, Guid turnId, CancellationToken cancellationToken) =>
            Task.FromResult(_turn!);

        public Task CompleteAsync(Guid userId, Guid turnId, string terminalEvent, CancellationToken cancellationToken)
        {
            _terminalEvents.Add(terminalEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class ChatAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Request.Headers["X-Test-User"].ToString()),
                new Claim("auth_method", "jwt")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
