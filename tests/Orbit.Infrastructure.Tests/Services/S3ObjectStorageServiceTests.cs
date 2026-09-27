using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Orbit.Application.Uploads.Common;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class S3ObjectStorageServiceTests
{
    private const string Bucket = "orbit-uploads-staging-713285551626";
    private const string Key = "2b3b1fc7-813c-4eb6-ad02-bd7754299703/image.webp";

    [Fact]
    public async Task CreateSignedUploadAsync_SignsPutTypeSizeAndShortExpiry()
    {
        using var client = CreateClient();
        var service = CreateService(client);
        var before = TimeProvider.System.GetUtcNow();

        var result = await service.CreateSignedUploadAsync(Key, "image/webp", 1024);

        var upload = new Uri(result.SignedUrl);
        var query = ParseQuery(upload);
        result.Key.Should().Be(Key);
        upload.Host.Should().Contain(Bucket);
        upload.AbsolutePath.Should().EndWith(Key);
        query["X-Amz-Credential"].Should().Contain("/us-east-2/s3/aws4_request");
        query["X-Amz-Expires"].Should().Be("600");
        query["X-Amz-SignedHeaders"].Split(';').Should().Contain(["content-type", "content-length", "host"]);
        DateTimeOffset.ParseExact(query["X-Amz-Date"], "yyyyMMddTHHmmssZ", null)
            .Should().BeCloseTo(before, TimeSpan.FromSeconds(5));
        result.PublicUrl.Should().Contain(Bucket).And.Contain(Key);
        ParseQuery(new Uri(result.PublicUrl))["X-Amz-Expires"].Should().Be("604800");
    }

    [Fact]
    public async Task CreateSignedUploadAsync_RejectsOversizeBeforeSigning()
    {
        using var client = CreateClient();
        var service = CreateService(client);

        var act = () => service.CreateSignedUploadAsync(Key, "image/webp", UploadContentTypes.MaxSizeBytes + 1);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task CreateSignedUploadAsync_RejectsUnsupportedContentType()
    {
        using var client = CreateClient();
        var service = CreateService(client);

        var act = () => service.CreateSignedUploadAsync(Key, "application/pdf", 1024);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private static AmazonS3Client CreateClient() => new(
        new BasicAWSCredentials("AKIAEXAMPLE123456789", "example-secret-key"),
        RegionEndpoint.USEast2);

    private static S3ObjectStorageService CreateService(AmazonS3Client client) => new(client,
        Options.Create(new S3StorageSettings { Bucket = Bucket, Region = "us-east-2" }));

    private static Dictionary<string, string> ParseQuery(Uri uri) => uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1]));
}
