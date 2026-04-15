using FileSignatures;
using FileSignatures.Formats;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Services;

public sealed class ImageValidationService : IImageValidationService
{
    private const long MaxFileSizeBytes = 20_971_520; // 20MB (AI provider inline data limit)
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp"
    };

    private static readonly FileFormatInspector Inspector = new();

    public Task<Result<(string MimeType, long Size)>> ValidateAsync(Stream stream, string fileName, long length)
    {
        // 1. Size check
        if (length > MaxFileSizeBytes)
            return Task.FromResult(Result.Failure<(string, long)>(
                $"File size exceeds maximum allowed size of {MaxFileSizeBytes / (1024 * 1024)}MB."));

        if (length == 0)
            return Task.FromResult(Result.Failure<(string, long)>("File is empty."));

        // 2. Extension check
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension) || !AllowedExtensions.Contains(extension))
            return Task.FromResult(Result.Failure<(string, long)>(
                $"File extension '{extension}' is not allowed. Allowed: {string.Join(", ", AllowedExtensions)}."));

        // 3. Magic bytes signature check using FileSignatures library
        var format = Inspector.DetermineFileFormat(stream);

        if (format == null)
            return Task.FromResult(Result.Failure<(string, long)>(
                "Unable to determine file format from magic bytes."));

        // Validate it's an image format
        if (format is not Image)
            return Task.FromResult(Result.Failure<(string, long)>(
                $"File is not a recognized image format. Detected: {format.GetType().Name}"));

        return Task.FromResult(Result.Success((format.MediaType, length)));
    }
}
