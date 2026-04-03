using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class GeoLocationServiceTests
{
    private readonly GeoLocationService _sut;
    private readonly FakeGeoHttpHandler _handler;

    public GeoLocationServiceTests()
    {
        _handler = new FakeGeoHttpHandler();
        var httpClient = new HttpClient(_handler);
        var logger = new NullLoggerFactory().CreateLogger<GeoLocationService>();
        _sut = new GeoLocationService(httpClient, logger);
    }

    [Fact]
    public async Task GetCountryCodeAsync_NullIp_ReturnsUS()
    {
        var result = await _sut.GetCountryCodeAsync(null);
        result.Should().Be("US");
    }

    [Fact]
    public async Task GetCountryCodeAsync_EmptyIp_ReturnsUS()
    {
        var result = await _sut.GetCountryCodeAsync("");
        result.Should().Be("US");
    }

    [Fact]
    public async Task GetCountryCodeAsync_WhitespaceIp_ReturnsUS()
    {
        var result = await _sut.GetCountryCodeAsync("   ");
        result.Should().Be("US");
    }

    [Fact]
    public async Task GetCountryCodeAsync_Localhost_ReturnsUS()
    {
        var result = await _sut.GetCountryCodeAsync("127.0.0.1");
        result.Should().Be("US");
    }

    [Fact]
    public async Task GetCountryCodeAsync_IPv6Localhost_ReturnsUS()
    {
        var result = await _sut.GetCountryCodeAsync("::1");
        result.Should().Be("US");
    }

    [Fact]
    public async Task GetCountryCodeAsync_SuccessfulLookup_ReturnsCountryCode()
    {
        _handler.ResponseBody = "BR";
        _handler.StatusCode = HttpStatusCode.OK;

        var result = await _sut.GetCountryCodeAsync("200.100.50.25");

        result.Should().Be("BR");
    }

    [Fact]
    public async Task GetCountryCodeAsync_ApiFailure_ReturnsUS()
    {
        _handler.StatusCode = HttpStatusCode.InternalServerError;

        var result = await _sut.GetCountryCodeAsync("200.100.50.25");

        result.Should().Be("US");
    }

    [Fact]
    public async Task GetCountryCodeAsync_EmptyResponseBody_ReturnsUS()
    {
        _handler.ResponseBody = "  ";
        _handler.StatusCode = HttpStatusCode.OK;

        var result = await _sut.GetCountryCodeAsync("200.100.50.25");

        result.Should().Be("US");
    }

    [Fact]
    public async Task GetCountryCodeAsync_HttpException_ReturnsUS()
    {
        _handler.ExceptionToThrow = new HttpRequestException("Network error");

        var result = await _sut.GetCountryCodeAsync("200.100.50.25");

        result.Should().Be("US");
    }

    [Fact]
    public async Task GetCountryCodeAsync_ValidIp_CallsCorrectUrl()
    {
        _handler.ResponseBody = "DE";
        _handler.StatusCode = HttpStatusCode.OK;

        await _sut.GetCountryCodeAsync("8.8.8.8");

        _handler.LastRequestUri.Should().Contain("8.8.8.8");
        _handler.LastRequestUri.Should().Contain("/country/");
    }

    private class FakeGeoHttpHandler : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public string ResponseBody { get; set; } = "US";
        public Exception? ExceptionToThrow { get; set; }
        public string? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (ExceptionToThrow is not null)
                throw ExceptionToThrow;

            LastRequestUri = request.RequestUri?.ToString();

            return Task.FromResult(new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(ResponseBody)
            });
        }
    }
}
