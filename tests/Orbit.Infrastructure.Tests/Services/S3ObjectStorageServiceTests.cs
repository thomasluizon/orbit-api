using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using NSubstitute;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Orbit.Application.Uploads.Common;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class S3ObjectStorageServiceTests
{
    private const string Bucket = "orbit-uploads-staging-713285551626";
    private const string Key = "2b3b1fc7-813c-4eb6-ad02-bd7754299703/1e21a217-e146-43c9-b61c-b5871a991578.webp";

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
        result.PublicUrl.Should().Be($"https://api-staging.useorbit.org/api/uploads/object/{Key}");

        var readUrl = new Uri(await service.CreateReadUrlAsync(Key));
        readUrl.Host.Should().Contain(Bucket);
        readUrl.AbsolutePath.Should().EndWith(Key);
        ParseQuery(readUrl)["X-Amz-Expires"].Should().Be("600");
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

    [Fact]
    public async Task CreateSignedUploadAsync_BindsDeclaredSizeInSignature()
    {
        using var client = CreateClient();
        var service = CreateService(client);

        var first = await service.CreateSignedUploadAsync(Key, "image/webp", 1024);
        var second = await service.CreateSignedUploadAsync(Key, "image/webp", 2048);

        ParseQuery(new Uri(first.SignedUrl))["X-Amz-Signature"]
            .Should().NotBe(ParseQuery(new Uri(second.SignedUrl))["X-Amz-Signature"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1001)]
    public async Task DeleteUserObjectsAsync_DrainsAllPagesAndPreservesOtherUsers(int count)
    {
        var userId = Guid.NewGuid();
        var prefix = $"{userId}/";
        var keys = Enumerable.Range(0, count).Select(i => $"{prefix}nested/{i}.png").ToHashSet();
        var other = $"{Guid.NewGuid()}/keep.png";
        keys.Add(other);
        var client = Substitute.For<IAmazonS3>();
        client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(call => new ListObjectsV2Response
            {
                S3Objects = keys.Where(key => key.StartsWith(call.Arg<ListObjectsV2Request>().Prefix, StringComparison.Ordinal))
                    .Take(1000).Select(key => new S3Object { Key = key }).ToList(),
            });
        client.DeleteObjectsAsync(Arg.Any<DeleteObjectsRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<DeleteObjectsRequest>();
                request.BucketName.Should().Be(Bucket);
                request.Objects.Should().HaveCountLessThanOrEqualTo(1000);
                foreach (var item in request.Objects)
                    keys.Remove(item.Key);
                return new DeleteObjectsResponse();
            });

        await CreateService(client).DeleteUserObjectsAsync(userId);

        keys.Should().BeEquivalentTo([other]);
        await client.Received(count == 0 ? 1 : count > 1000 ? 3 : 2).ListObjectsV2Async(
            Arg.Is<ListObjectsV2Request>(request => request.BucketName == Bucket && request.Prefix == prefix
                && request.Delimiter == null && request.ContinuationToken == null && request.MaxKeys == 1000),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteUserObjectsAsync_PropagatesPartialDeleteErrors()
    {
        var client = Substitute.For<IAmazonS3>();
        var userId = Guid.NewGuid();
        client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(new ListObjectsV2Response { S3Objects = [new S3Object { Key = $"{userId}/one.png" }] });
        client.DeleteObjectsAsync(Arg.Any<DeleteObjectsRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<DeleteObjectsResponse>>(_ => throw new DeleteObjectsException(new DeleteObjectsResponse
            {
                DeleteErrors = [new DeleteError { Key = $"{userId}/one.png", Code = "AccessDenied" }],
            }));

        var act = () => CreateService(client).DeleteUserObjectsAsync(userId);

        await act.Should().ThrowAsync<DeleteObjectsException>();
    }

    [Fact]
    public async Task DeleteUserObjectsAsync_RefusesSuccessWhenObjectsRemain()
    {
        var client = Substitute.For<IAmazonS3>();
        var userId = Guid.NewGuid();
        client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(new ListObjectsV2Response { S3Objects = [new S3Object { Key = $"{userId}/one.png" }] });
        client.DeleteObjectsAsync(Arg.Any<DeleteObjectsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DeleteObjectsResponse());

        var act = () => CreateService(client).DeleteUserObjectsAsync(userId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*same objects*");
    }

    [Fact]
    public async Task DeleteUserObjectsAsync_RejectsEmptyOwner()
    {
        using var client = CreateClient();
        var act = () => CreateService(client).DeleteUserObjectsAsync(Guid.Empty);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task PrivateConversationObjects_ReuseBucketAndNeverCreatePublicAclOrUrl()
    {
        var client = Substitute.For<IAmazonS3>();
        var key = $"{Guid.NewGuid()}/conversations/{Guid.NewGuid()}/{Guid.NewGuid()}.png";
        byte[] bytes = [1, 2, 3, 4];
        byte[]? uploaded = null;
        client.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var request = call.Arg<PutObjectRequest>();
            request.BucketName.Should().Be(Bucket);
            request.Key.Should().Be(key);
            request.ContentType.Should().Be("image/png");
            request.CannedACL.Should().BeNull();
            using var copy = new MemoryStream();
            await request.InputStream.CopyToAsync(copy);
            uploaded = copy.ToArray();
            return new PutObjectResponse();
        });
        client.GetObjectAsync(Bucket, key, Arg.Any<CancellationToken>()).Returns(new GetObjectResponse { ResponseStream = new MemoryStream(bytes) });
        client.DeleteObjectAsync(Bucket, key, Arg.Any<CancellationToken>()).Returns(new DeleteObjectResponse());
        var service = CreateService(client);
        await service.PutPrivateAsync(key, "image/png", bytes);
        uploaded.Should().Equal(bytes);
        (await service.ReadPrivateAsync(key)).Should().Equal(bytes);
        await service.DeletePrivateAsync(key);
        await client.Received(1).DeleteObjectAsync(Bucket, key, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteConversationObjects_RemovesOrphansWithinOnlyTheRequestedPrefix(bool oneConversation)
    {
        var user = Guid.NewGuid();
        var conversation = Guid.NewGuid();
        var prefix = $"{user}/conversations/";
        var keys = new HashSet<string>
        {
            $"{prefix}{conversation}/orphan.png", $"{prefix}{Guid.NewGuid()}/another.png",
            $"{user}/ordinary-upload.png", $"{Guid.NewGuid()}/conversations/{conversation}/keep.png"
        };
        var expected = oneConversation ? $"{prefix}{conversation}/" : prefix;
        var client = Substitute.For<IAmazonS3>();
        client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<ListObjectsV2Request>().Prefix.Should().Be(expected);
            return new ListObjectsV2Response
            {
                S3Objects = keys.Where(key => key.StartsWith(expected, StringComparison.Ordinal))
                .Select(key => new S3Object { Key = key }).ToList()
            };
        });
        client.DeleteObjectsAsync(Arg.Any<DeleteObjectsRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            foreach (var item in call.Arg<DeleteObjectsRequest>().Objects) keys.Remove(item.Key);
            return new DeleteObjectsResponse();
        });
        await CreateService(client).DeleteConversationObjectsAsync(user, oneConversation ? conversation : null);
        keys.Should().HaveCount(oneConversation ? 3 : 2).And.NotContain(key => key.StartsWith(expected, StringComparison.Ordinal));
    }

    private static AmazonS3Client CreateClient() => new(
        new BasicAWSCredentials("AKIAEXAMPLE123456789", "example-secret-key"),
        RegionEndpoint.USEast2);

    private static S3ObjectStorageService CreateService(IAmazonS3 client) => new(client,
        Options.Create(new S3StorageSettings
        {
            Bucket = Bucket,
            Region = "us-east-2",
            PublicBaseUrl = "https://api-staging.useorbit.org",
        }));

    private static Dictionary<string, string> ParseQuery(Uri uri) => uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1]));
}
