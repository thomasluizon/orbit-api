using Orbit.Domain.Common;

namespace Orbit.Domain.Interfaces;

public interface IImageValidationService
{
    /// <summary>
    /// Validate the file by extension + length + magic-bytes sniffing. The caller opens
    /// the stream from whatever upload type the framework provides (ASP.NET IFormFile,
    /// a memory stream, etc.); the service doesn't depend on a web framework so Domain
    /// stays framework-free.
    /// </summary>
    /// <param name="stream">Readable stream positioned at the start of the file.</param>
    /// <param name="fileName">Original file name, used only for extension validation.</param>
    /// <param name="length">Total file length in bytes.</param>
    Task<Result<(string MimeType, long Size)>> ValidateAsync(Stream stream, string fileName, long length);
}
