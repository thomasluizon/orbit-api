using Orbit.Domain.Common;

namespace Orbit.Domain.Interfaces;

public interface IImageValidationService
{
    Task<Result<(string MimeType, long Size)>> ValidateAsync(Stream stream, string fileName, long length);
}
