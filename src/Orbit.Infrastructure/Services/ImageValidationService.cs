using FileSignatures;
using FileSignatures.Formats;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Services;

public sealed class ImageValidationService : IImageValidationService
{
    private const long MaxFileSizeBytes = 20_971_520;    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp"
    };

    private static readonly FileFormatInspector Inspector = new();

    public Task<Result<(string MimeType, long Size)>> ValidateAsync(Stream stream, string fileName, long length)
    {
        if (length > MaxFileSizeBytes)
            return Task.FromResult(Result.Failure<(string, long)>(
                $"File size exceeds maximum allowed size of {MaxFileSizeBytes / (1024 * 1024)}MB."));

        if (length == 0)
            return Task.FromResult(Result.Failure<(string, long)>("File is empty."));

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension) || !AllowedExtensions.Contains(extension))
            return Task.FromResult(Result.Failure<(string, long)>(
                $"File extension '{extension}' is not allowed. Allowed: {string.Join(", ", AllowedExtensions)}."));

        var format = Inspector.DetermineFileFormat(stream);

        if (format == null)
            return Task.FromResult(Result.Failure<(string, long)>(
                "Unable to determine file format from magic bytes."));

        if (format is not Image)
            return Task.FromResult(Result.Failure<(string, long)>(
                $"File is not a recognized image format. Detected: {format.GetType().Name}"));

        return Task.FromResult(Result.Success((format.MediaType, length)));
    }
}
