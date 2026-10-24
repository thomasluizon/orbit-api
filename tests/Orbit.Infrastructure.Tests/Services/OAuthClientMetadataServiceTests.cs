using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Auth.Validators;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class OAuthClientMetadataServiceTests : IDisposable
{
    public const string ClientId = "https://claude.ai/oauth/mcp-oauth-client-metadata";
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly IHttpClientFactory _factory = Substitute.For<IHttpClientFactory>();
    private readonly ResponseHandler _handler = new();
    private readonly OAuthClientMetadataService _service;

    public OAuthClientMetadataServiceTests()
    {
        _factory.CreateClient(OAuthClientMetadataService.HttpClientName).Returns(_ => new HttpClient(_handler, false));
        _service = new OAuthClientMetadataService(_factory, _cache, new OAuthClientMetadataDocumentValidator(), TimeProvider.System);
    }

    public static string ClaudeDocument => File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
        "Fixtures", "OAuth", "ClaudeClientMetadata.json"));

    [Fact]
    public async Task GetAsync_ValidClaudeDocument_RespectsMaxAge()
    {
        var metadata = await _service.GetAsync(ClientId, default);
        metadata!.ClientId.Should().Be(ClientId);
        metadata.ClientName.Should().Be("Claude");
        metadata.RedirectUris.Should().Equal("https://claude.ai/api/mcp/auth_callback");
        (await _service.GetAsync(ClientId, default)).Should().BeSameAs(metadata);
        _handler.Requests.Should().Be(1);
    }

    [Theory]
    [InlineData("no-store")]
    [InlineData("no-cache")]
    [InlineData("private, max-age=300")]
    [InlineData("max-age=0")]
    public async Task GetAsync_DoesNotReuseUncacheableDocuments(string control)
    {
        _handler.CacheControl = control;
        (await _service.GetAsync(ClientId, default)).Should().NotBeNull();
        (await _service.GetAsync(ClientId, default)).Should().NotBeNull();
        _handler.Requests.Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_HonorsExpiresAndAge()
    {
        _handler.CacheControl = null;
        _handler.Expires = DateTimeOffset.UtcNow.AddMinutes(5);
        (await _service.GetAsync(ClientId, default)).Should().NotBeNull();
        (await _service.GetAsync(ClientId, default)).Should().NotBeNull();
        _handler.Requests.Should().Be(1);
        _cache.Clear();
        _handler.Age = TimeSpan.FromMinutes(10);
        await _service.GetAsync(ClientId, default);
        await _service.GetAsync(ClientId, default);
        _handler.Requests.Should().Be(3);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not JSON")]
    [InlineData("{\"client_id\":123}")]
    [InlineData("{\"client_id\":\"https://claude.ai/oauth/mcp-oauth-client-metadata\"}")]
    public async Task GetAsync_RejectsMalformedDocumentsWithoutCaching(string body)
    {
        _handler.Body = body;
        (await _service.GetAsync(ClientId, default)).Should().BeNull();
        _handler.Body = ClaudeDocument;
        (await _service.GetAsync(ClientId, default)).Should().NotBeNull();
        _handler.Requests.Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_RequiresExactClientIdAndPublicAuthentication()
    {
        foreach (var body in new[]
        {
            ClaudeDocument.Replace(ClientId, ClientId + "/"),
            ClaudeDocument.Replace("\"Claude\"", "null"),
            ClaudeDocument.Replace("\"none\"", "\"private_key_jwt\""),
            ClaudeDocument.Replace("\"none\"", "\"client_secret_post\""),
            ClaudeDocument.Insert(1, "\"client_secret\":\"secret\","),
            ClaudeDocument.Insert(1, "\"client_id\":\"duplicate\",")
        })
        {
            _handler.Body = body;
            (await _service.GetAsync(ClientId, default)).Should().BeNull();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetAsync_RejectsOversizedDocumentWithOrWithoutContentLength(bool knownLength)
    {
        _handler.Body = ClaudeDocument + new string(' ', OAuthClientMetadataService.MaxDocumentBytes);
        _handler.Streamed = !knownLength;
        (await _service.GetAsync(ClientId, default)).Should().BeNull();
    }

    [Theory]
    [InlineData(302)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task GetAsync_RejectsErrorsAndRedirects(int status)
    {
        _handler.Status = (HttpStatusCode)status;
        (await _service.GetAsync(ClientId, default)).Should().BeNull();
        (await _service.GetAsync(ClientId, default)).Should().BeNull();
        _handler.Requests.Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_RejectsPrivateAddressThroughProductionHandler()
    {
        using var handler = OAuthMetadataNetwork.CreateHandler();
        _factory.CreateClient(OAuthClientMetadataService.HttpClientName).Returns(_ => new HttpClient(handler, false));
        (await _service.GetAsync("https://127.0.0.1/client.json", default)).Should().BeNull();
        (await _service.GetAsync("https://[::1]/client.json", default)).Should().BeNull();
        handler.AllowAutoRedirect.Should().BeFalse();
        handler.UseProxy.Should().BeFalse();
    }

    [Fact]
    public async Task GetAsync_EnforcesDeadlineWhileReadingBody()
    {
        _handler.StallBody = true;
        (await _service.GetAsync(ClientId, default)).Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_PropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var fetch = () => _service.GetAsync(ClientId, cancellation.Token);
        await fetch.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("2002:7f00:1::1")]
    public async Task ResolvePublicAddressesAsync_RejectsPrivateDnsAnswersIncludingMixedAnswers(string address)
    {
        var resolve = () => OAuthMetadataNetwork.ResolvePublicAddressesAsync("client.example",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("1.1.1.1"), IPAddress.Parse(address) }), default);
        await resolve.Should().ThrowAsync<HttpRequestException>();
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public async Task ResolvePublicAddressesAsync_ReturnsValidatedAddressesForConnection(string address)
    {
        var addresses = new[] { IPAddress.Parse(address) };
        (await OAuthMetadataNetwork.ResolvePublicAddressesAsync("client.example",
            (_, _) => Task.FromResult(addresses), default)).Should().BeSameAs(addresses);
    }

    public void Dispose()
    {
        _handler.Dispose();
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class ResponseHandler : HttpMessageHandler
    {
        public string Body { get; set; } = ClaudeDocument;
        public string? CacheControl { get; set; } = "public, max-age=300";
        public DateTimeOffset? Expires { get; set; }
        public TimeSpan? Age { get; set; }
        public bool Streamed { get; set; }
        public bool StallBody { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            request.RequestUri!.OriginalString.Should().Be(ClientId);
            var content = StallBody ? new StreamContent(new StalledStream()) : Streamed
                ? (HttpContent)new StreamContent(new UnseekableStream(Encoding.UTF8.GetBytes(Body)))
                : new StringContent(Body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            content.Headers.Expires = Expires;
            var response = new HttpResponseMessage(Status) { Content = content };
            response.Headers.CacheControl = CacheControl is null ? null : CacheControlHeaderValue.Parse(CacheControl);
            response.Headers.Date = DateTimeOffset.UtcNow;
            response.Headers.Age = Age;
            response.Headers.Location = new Uri("https://other.example/client.json");
            return Task.FromResult(response);
        }
    }

    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class StalledStream : MemoryStream
    {
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
