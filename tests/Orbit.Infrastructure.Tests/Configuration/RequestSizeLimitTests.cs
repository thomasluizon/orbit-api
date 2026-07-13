using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orbit.Application.Uploads.Common;
using OrbitServiceCollectionExtensions = Orbit.Api.Extensions.ServiceCollectionExtensions;

namespace Orbit.Infrastructure.Tests.Configuration;

/// <summary>
/// Guards the request-body size ceiling the API registers with Kestrel and its
/// relationship to the stricter per-upload content-type limit. A regression that
/// drops the transport ceiling or lets it fall to/below the upload max must fail here.
/// </summary>
public class RequestSizeLimitTests
{
    private const long ExpectedMaxRequestBodyBytes = 10 * 1024 * 1024;

    private static long RegisteredMaxRequestBodyBytes()
    {
        var builder = WebApplication.CreateBuilder();

        OrbitServiceCollectionExtensions.AddCookieAndKestrelLimits(builder);

        using var provider = builder.Services.BuildServiceProvider();
        var configuredLimit = provider
            .GetRequiredService<IOptions<KestrelServerOptions>>()
            .Value.Limits.MaxRequestBodySize;

        return configuredLimit
            ?? throw new InvalidOperationException("Kestrel MaxRequestBodySize was left unbounded.");
    }

    [Fact]
    public void AddCookieAndKestrelLimits_RegistersTenMegabyteRequestBodyCeiling()
    {
        RegisteredMaxRequestBodyBytes().Should().Be(ExpectedMaxRequestBodyBytes);
    }

    [Fact]
    public void RegisteredRequestBodyCeiling_LeavesHeadroomAboveUploadContentTypeMax()
    {
        RegisteredMaxRequestBodyBytes().Should().BeGreaterThan(
            UploadContentTypes.MaxSizeBytes,
            "the request-body ceiling must sit above the upload content-type max so an oversize upload is rejected by the validator, not truncated by the transport");
    }
}
