using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Orbit.Application.Uploads.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed class S3ObjectStorageService(IAmazonS3 client, IOptions<S3StorageSettings> options) : IObjectStorageService, IObjectStorageReadService
{
    private static readonly TimeSpan UploadLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ReadLifetime = TimeSpan.FromMinutes(10);
    private readonly S3StorageSettings _settings = options.Value;

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
