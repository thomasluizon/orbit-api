using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Orbit.Application.Uploads.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed class S3ObjectStorageService(IAmazonS3 client, IOptions<S3StorageSettings> options) : IObjectStorageService, IObjectStorageReadService, IObjectStorageDeletionService
{
    private static readonly TimeSpan UploadLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ReadLifetime = TimeSpan.FromMinutes(10);
    private readonly S3StorageSettings _settings = options.Value;

    public async Task PutPrivateAsync(string objectKey, string contentType, byte[] data, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream(data, writable: false);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _settings.Bucket,
            Key = objectKey,
            ContentType = contentType,
            InputStream = stream
        }, cancellationToken);
    }

    public async Task<byte[]> ReadPrivateAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetObjectAsync(_settings.Bucket, objectKey, cancellationToken);
        using var stream = new MemoryStream();
        await response.ResponseStream.CopyToAsync(stream, cancellationToken);
        return stream.ToArray();
    }

    public async Task DeletePrivateAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        await client.DeleteObjectAsync(_settings.Bucket, objectKey, cancellationToken);
    }

    public async Task<SignedUpload> CreateSignedUploadAsync(
        string objectKey, string contentType, long sizeBytes, CancellationToken cancellationToken = default)
    {
        if (!UploadContentTypes.IsAllowed(contentType))
            throw new ArgumentException("Unsupported upload content type.", nameof(contentType));
        if (sizeBytes is <= 0 or > UploadContentTypes.MaxSizeBytes)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));

        var now = TimeProvider.System.GetUtcNow().UtcDateTime;
        var uploadUrl = await client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = _settings.Bucket,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = now.Add(UploadLifetime),
            Headers = { ContentLength = sizeBytes },
        });
        return new SignedUpload(objectKey, uploadUrl,
            $"{_settings.PublicBaseUrl.TrimEnd('/')}/api/uploads/object/{objectKey}");
    }

    public async Task DeleteUserObjectsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("A user id is required.", nameof(userId));
        ArgumentException.ThrowIfNullOrWhiteSpace(_settings.Bucket);

        var prefix = $"{userId}/";
        HashSet<string>? previousKeys = null;
        while (true)
        {
            var page = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _settings.Bucket,
                Prefix = prefix,
                MaxKeys = 1000,
            }, cancellationToken);
            var keys = page.S3Objects?.Select(value => value.Key).ToHashSet(StringComparer.Ordinal) ?? [];
            if (keys.Count == 0)
                return;
            if (keys.Any(key => !key.StartsWith(prefix, StringComparison.Ordinal)))
                throw new InvalidOperationException("Storage returned an object outside the user's prefix.");
            if (previousKeys is not null && previousKeys.SetEquals(keys))
                throw new InvalidOperationException("Storage deletion left the same objects behind.");

            await client.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = _settings.Bucket,
                Objects = keys.Select(key => new KeyVersion { Key = key }).ToList(),
                Quiet = true,
            }, cancellationToken);
            previousKeys = keys;
        }
    }

    public Task<string> CreateReadUrlAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        return client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = _settings.Bucket,
            Key = objectKey,
            Verb = HttpVerb.GET,
            Expires = TimeProvider.System.GetUtcNow().UtcDateTime.Add(ReadLifetime),
        });
    }
}
