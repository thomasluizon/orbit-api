namespace Orbit.Domain.Interfaces;

public sealed record SignedUpload(string Key, string SignedUrl, string PublicUrl);

public interface IObjectStorageService
{
    Task<SignedUpload> CreateSignedUploadAsync(string objectKey, CancellationToken cancellationToken = default);
}
