namespace Orbit.Domain.Interfaces;

public sealed record SignedUpload(string Key, string SignedUrl, string PublicUrl);

public interface IObjectStorageService
{
    Task PutPrivateAsync(string objectKey, string contentType, byte[] data, CancellationToken cancellationToken = default);
    Task<byte[]> ReadPrivateAsync(string objectKey, CancellationToken cancellationToken = default);
    Task DeletePrivateAsync(string objectKey, CancellationToken cancellationToken = default);
    Task<SignedUpload> CreateSignedUploadAsync(string objectKey, string contentType, long sizeBytes, CancellationToken cancellationToken = default);
}

public interface IObjectStorageReadService
{
    Task<string> CreateReadUrlAsync(string objectKey, CancellationToken cancellationToken = default);
}

public interface IObjectStorageDeletionService
{
    Task DeleteUserObjectsAsync(Guid userId, CancellationToken cancellationToken = default);
}
