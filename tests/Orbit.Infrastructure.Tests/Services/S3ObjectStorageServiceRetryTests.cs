using System.Net;
using System.Xml.Linq;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class S3ObjectStorageServiceRetryTests
{
    private const string Bucket = "uploads-test";
    private static readonly XNamespace S3Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    private static AmazonS3Client CreateClient(HttpMessageHandler handler) => new(
        new BasicAWSCredentials("test-access-key", "test-secret"),
        new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.USEast2,
            HttpClientFactory = new TestHttpClientFactory(handler),
            MaxErrorRetry = 2,
        });

    private static S3ObjectStorageService CreateService(IAmazonS3 client) => new(client,
        Options.Create(new S3StorageSettings
        {
            Bucket = Bucket,
            Region = "us-east-2",
            PublicBaseUrl = "https://api.example.com",
        }));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1001)]
    public async Task DeleteUserObjectsAsync_DrainsAllPagesIncludingNestedKeys(int count)
    {
        var userId = Guid.NewGuid();
        var other = $"{Guid.NewGuid()}/keep.png";
        var keys = Enumerable.Range(0, count).Select(i => $"{userId}/nested/{i}.png").ToHashSet();
        keys.Add(other);
        using var handler = new ObjectStoreHandler(keys);
        using var client = CreateClient(handler);

        await CreateService(client).DeleteUserObjectsAsync(userId);

        keys.Should().BeEquivalentTo([other]);
        handler.Listings.Should().Be(count == 0 ? 1 : count > 1000 ? 3 : 2);
        handler.Deletions.Should().Be(count == 0 ? 0 : count > 1000 ? 2 : 1);
    }

    [Fact]
    public async Task DeleteUserObjectsAsync_RetriesFailedDeleteAndVerifiesEmptyPrefix()
    {
        var userId = Guid.NewGuid();
        using var handler = new ObjectStoreHandler([$"{userId}/one.png"]) { FailuresRemaining = 1 };
        using var client = CreateClient(handler);

        await CreateService(client).DeleteUserObjectsAsync(userId);

        handler.Deletions.Should().Be(2);
        handler.Keys.Should().BeEmpty();
        handler.Listings.Should().Be(2);
    }

    [Fact]
    public async Task DeleteUserObjectsAsync_PersistentFailureDoesNotReportSuccess()
    {
        var userId = Guid.NewGuid();
        using var handler = new ObjectStoreHandler([$"{userId}/one.png"]) { FailuresRemaining = 3 };
        using var client = CreateClient(handler);
        var act = () => CreateService(client).DeleteUserObjectsAsync(userId);

        await act.Should().ThrowAsync<AmazonS3Exception>();
        handler.Deletions.Should().Be(3);
        handler.Keys.Should().HaveCount(1);
    }

    [Fact]
    public async Task DeleteUserObjectsAsync_NoProgressDoesNotReportSuccess()
    {
        var userId = Guid.NewGuid();
        using var handler = new ObjectStoreHandler([$"{userId}/one.png"]) { KeepObjects = true };
        using var client = CreateClient(handler);
        var act = () => CreateService(client).DeleteUserObjectsAsync(userId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*same objects*");
    }

    [Fact]
    public async Task DeleteUserObjectsAsync_RejectsEmptyOwner()
    {
        using var handler = new ObjectStoreHandler([]);
        using var client = CreateClient(handler);
        var act = () => CreateService(client).DeleteUserObjectsAsync(Guid.Empty);

        await act.Should().ThrowAsync<ArgumentException>();
        handler.Listings.Should().Be(0);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(null)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task CreateSignedUploadAsync_SignsLocallyRegardlessOfStorageAvailability(HttpStatusCode? failure)
    {
        using var handler = new UnavailableHandler(failure);
        using var client = CreateClient(handler);

        var result = await CreateService(client).CreateSignedUploadAsync("key", "image/png", 1024);

        result.SignedUrl.Should().Contain("X-Amz-Signature=");
        result.PublicUrl.Should().Be("https://api.example.com/api/uploads/object/key");
        handler.CallCount.Should().Be(0);
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : Amazon.Runtime.HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(handler, disposeHandler: false);
    }

    private sealed class UnavailableHandler(HttpStatusCode? failure) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (failure is null)
                throw new HttpRequestException("Storage unavailable");
            return Task.FromResult(new HttpResponseMessage(failure.Value));
        }
    }

    private sealed class ObjectStoreHandler(HashSet<string> keys) : HttpMessageHandler
    {
        public HashSet<string> Keys => keys;
        public int Listings { get; private set; }
        public int Deletions { get; private set; }
        public int FailuresRemaining { get; set; }
        public bool KeepObjects { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                Listings++;
                var query = request.RequestUri!.Query.TrimStart('?').Split('&')
                    .Select(pair => pair.Split('=', 2))
                    .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
                query["list-type"].Should().Be("2");
                query["max-keys"].Should().Be("1000");
                query.Should().NotContainKey("delimiter");
                var objects = keys.Where(key => key.StartsWith(query["prefix"], StringComparison.Ordinal))
                    .Take(int.Parse(query["max-keys"]));
                return XmlResponse(HttpStatusCode.OK, new XElement(S3Namespace + "ListBucketResult",
                    objects.Select(key => new XElement(S3Namespace + "Contents", new XElement(S3Namespace + "Key", key)))));
            }

            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.Query.Should().Contain("delete");
            var body = XDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            body.Root!.Element(S3Namespace + "Quiet")!.Value.Should().Be("true");
            Deletions++;
            if (FailuresRemaining-- > 0)
                return XmlResponse(HttpStatusCode.ServiceUnavailable,
                    new XElement("Error", new XElement("Code", "ServiceUnavailable")));
            if (!KeepObjects)
                foreach (var key in body.Descendants(S3Namespace + "Key"))
                    keys.Remove(key.Value);
            return XmlResponse(HttpStatusCode.OK, new XElement(S3Namespace + "DeleteResult"));
        }

        private static HttpResponseMessage XmlResponse(HttpStatusCode status, XElement body) => new(status)
        {
            Content = new StringContent(body.ToString(), System.Text.Encoding.UTF8, "application/xml"),
        };
    }
}
