namespace Orbit.Domain.Interfaces;

public sealed record SignedUpload(string Key, string SignedUrl, string PublicUrl);

public interface IObjectStorageService
{
    Task<SignedUpload> CreateSignedUploadAsync(string objectKey, string contentType, long sizeBytes, CancellationToken cancellationToken = default);
}

public interface IObjectStorageReadService
{
    Task<string> CreateReadUrlAsync(string objectKey, CancellationToken cancellationToken = default);
}
